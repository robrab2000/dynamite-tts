//! The speech pipeline: cleanup, optional Summary / Explain through the built-in chat model
//! (llama.cpp in-process) or Lemonade's, then Phonon (in-process) streamed into the player.
//!
//! Public methods run on the UI thread. Each request runs on a worker thread and reports back
//! through [`Event`]s tagged with its generation; a newer request or a stop bumps the
//! generation, which makes the old worker stop at its next check and its late events harmless.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, Instant};

use crate::audio::{Player, Session};
use crate::settings::{ChatBackend, Settings, SpeakMode};
use crate::{lemonade, llm, log, phonon, text};

/// Synthesis runs this far ahead of playback at most, so a stop never strands minutes of audio.
const MAX_QUEUED_SECONDS: f64 = 8.0;

#[derive(Debug, Clone, PartialEq)]
pub enum Event {
    /// The phase the status pill and tray show; None is "ready".
    Phase(u64, Option<String>),
    Notice(u64, String),
}

pub type Emit = Arc<dyn Fn(Event) + Send + Sync>;

pub struct Speech {
    generation: Arc<AtomicU64>,
    phase: Option<String>,
    player: Arc<Player>,
    engine: Arc<phonon::Engine>,
    client: Arc<lemonade::Client>,
    llm: Arc<llm::Engine>,
    received_audio: Arc<AtomicBool>,
    emit: Emit,
}

impl Speech {
    pub fn new(emit: Emit) -> Speech {
        Speech {
            generation: Arc::new(AtomicU64::new(0)),
            phase: None,
            player: Arc::new(Player::new()),
            engine: Arc::new(phonon::Engine::default()),
            client: Arc::new(lemonade::Client::new()),
            llm: Arc::new(llm::Engine::default()),
            received_audio: Arc::new(AtomicBool::new(false)),
            emit,
        }
    }

    /// Frees the built-in chat model after a while unused. Returns when to call again.
    pub fn maintain(&self) -> Option<Instant> {
        self.llm.unload_if_idle(llm::IDLE_UNLOAD)
    }

    /// Starts llama.cpp in the background when Summary or Explain may be used soon, so the first
    /// request doesn't wait for its GPU set-up (a few seconds on a Mac, once per run).
    pub fn prepare_chat(&self, settings: &Settings) {
        if settings.chat_backend == ChatBackend::Local && settings.speak_mode != SpeakMode::Verbatim {
            let _ = std::thread::Builder::new().name("llm-warm-up".into()).spawn(llm::warm_up);
        }
    }

    pub fn is_busy(&self) -> bool {
        self.phase.is_some()
    }

    pub fn phase(&self) -> Option<&str> {
        self.phase.as_deref()
    }

    /// Sets the tone control (gains in dB).
    pub fn set_eq(&self, low_db: f32, mid_db: f32, high_db: f32) {
        self.player.set_eq(low_db, mid_db, high_db);
    }

    /// Current playback loudness (0-1), for the status pill.
    pub fn level(&self) -> f32 {
        self.player.level()
    }

    pub fn generation(&self) -> u64 {
        self.generation.load(Ordering::SeqCst)
    }

    /// Applies a worker's event if it is still current. Returns the phase change to show, if any.
    pub fn accept(&mut self, event: &Event) -> bool {
        let generation = match event {
            Event::Phase(g, _) | Event::Notice(g, _) => *g,
        };
        if generation != self.generation() {
            return false;
        }
        if let Event::Phase(_, phase) = event {
            self.phase = phase.clone();
        }
        true
    }

    /// Speaks `raw` (with optional surrounding text for Explain) in `mode`.
    /// Returns a notice to show when there is nothing to say.
    pub fn speak(
        &mut self,
        raw: &str,
        context: Option<String>,
        settings: &Settings,
        mode: SpeakMode,
    ) -> Option<String> {
        let clean = text::sanitize(raw);
        if clean.is_empty() {
            return Some("No text selected".into());
        }
        self.stop(false, settings);
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        log!("speak chars={} mode={} voice={}", clean.chars().count(), mode.as_str(), settings.phonon_voice);
        // Busy before the worker starts, so a second shortcut stops this request rather than
        // racing a phase that has not been reported yet.
        self.phase = Some("Starting…".into());
        let worker = Worker {
            generation,
            current: self.generation.clone(),
            player: self.player.clone(),
            engine: self.engine.clone(),
            client: self.client.clone(),
            llm: self.llm.clone(),
            received_audio: self.received_audio.clone(),
            emit: self.emit.clone(),
            settings: settings.clone(),
            last_phase: std::sync::Mutex::new(String::new()),
        };
        let spawned = std::thread::Builder::new().name("speech".into()).spawn(move || {
            let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| worker.run(&clean, context, mode)))
                .unwrap_or_else(|_| Err("Unexpected error while speaking.".into()));
            if let Err(message) = outcome {
                if worker.alive() {
                    log!("speak failed: {message}");
                    worker.player.stop();
                    (worker.emit)(Event::Notice(generation, message));
                    (worker.emit)(Event::Phase(generation, None));
                }
            }
        });
        if spawned.is_err() {
            self.phase = None;
            return Some("Could not start speaking.".into());
        }
        None
    }

    /// Stops speech at once; `chime` plays the stop sound when audio had started.
    pub fn stop(&mut self, chime: bool, settings: &Settings) {
        self.generation.fetch_add(1, Ordering::SeqCst);
        self.player.stop();
        let had_audio = self.received_audio.swap(false, Ordering::SeqCst);
        if chime && had_audio && settings.play_sound_on_stop {
            self.player.chime(&settings.audio_device);
        }
        self.phase = None;
    }
}

struct Worker {
    generation: u64,
    current: Arc<AtomicU64>,
    player: Arc<Player>,
    engine: Arc<phonon::Engine>,
    client: Arc<lemonade::Client>,
    llm: Arc<llm::Engine>,
    received_audio: Arc<AtomicBool>,
    emit: Emit,
    settings: Settings,
    /// The last phase reported, so a repeat (say, loading announced twice) isn't sent again.
    last_phase: std::sync::Mutex<String>,
}

impl Worker {
    fn alive(&self) -> bool {
        self.current.load(Ordering::SeqCst) == self.generation
    }

    fn phase(&self, phase: &str) {
        let mut last = self.last_phase.lock().unwrap_or_else(|e| e.into_inner());
        if self.alive() && *last != phase {
            *last = phase.to_string();
            (self.emit)(Event::Phase(self.generation, Some(phase.to_string())));
        }
    }

    fn run(&self, text: &str, context: Option<String>, mode: SpeakMode) -> Result<(), String> {
        let cancelled = || !self.alive();
        let mut spoken = text.to_string();
        if mode != SpeakMode::Verbatim {
            let explain = mode == SpeakMode::Explain;
            let started = Instant::now();
            let reply = match self.settings.chat_backend {
                ChatBackend::Local => self.local_reply(text, context.as_deref(), mode),
                ChatBackend::Lemonade => self.lemonade_reply(text, context.as_deref(), mode),
            }?;
            if cancelled() {
                return Ok(());
            }
            log!(
                "{} ({}): {} chars -> {} chars in {} ms",
                mode.as_str(),
                self.settings.chat_backend.title(),
                text.chars().count(),
                reply.chars().count(),
                started.elapsed().as_millis()
            );
            spoken = text::sanitize(&reply);
            if spoken.is_empty() {
                return Err(if explain { "The explanation was empty." } else { "The summary was empty." }.into());
            }
        }
        if cancelled() {
            return Ok(());
        }

        let chunks = text::chunks(&spoken);
        self.speak_phonon(&chunks)
    }

    /// The system prompt, user message and reply limit for a Summary or Explain request.
    fn prompts(&self, text: &str, context: Option<&str>, mode: SpeakMode) -> (String, String, Option<u32>) {
        let s = &self.settings;
        if mode == SpeakMode::Explain {
            let n = s.explain_sentence_count;
            (
                text::explain_prompt(s.explain_audience, n),
                text::explain_user_message(text, context),
                Some(text::explain_max_tokens(n)),
            )
        } else {
            (text::SUMMARY_PROMPT.to_string(), text.to_string(), None)
        }
    }

    fn working_phase(mode: SpeakMode) -> &'static str {
        if mode == SpeakMode::Explain { "Explaining…" } else { "Summarizing…" }
    }

    fn local_reply(&self, text: &str, context: Option<&str>, mode: SpeakMode) -> Result<String, String> {
        let cancelled = || !self.alive();
        let (system, user, max_tokens) = self.prompts(text, context, mode);
        let model = llm::find_model(&self.settings.local_model);
        let mut last_percent = None;
        if !self.llm.is_loaded() && llm::is_downloaded(model) {
            self.phase("Loading the chat model…");
        }
        let reply = self.llm.chat(
            model.id,
            &system,
            &user,
            max_tokens.unwrap_or(text::SUMMARY_MAX_TOKENS),
            &cancelled,
            &mut |p| match p {
                llm::Progress::Downloading(f) => {
                    self.download_phase("Downloading the chat model", f, &mut last_percent);
                }
                llm::Progress::Loading => self.phase("Loading the chat model…"),
                llm::Progress::Writing => self.phase(Self::working_phase(mode)),
            },
        );
        reply.map_err(|e| self.chat_error(e.0, mode))
    }

    fn lemonade_reply(&self, text: &str, context: Option<&str>, mode: SpeakMode) -> Result<String, String> {
        let base = self.settings.base_url.as_str();
        let cancelled = || !self.alive();
        if !self.client.is_healthy(base) {
            self.phase("Starting Lemonade…");
        }
        self.client.ensure_server(base, &cancelled).map_err(|e| self.chat_error(e.0, mode))?;
        if cancelled() {
            return Ok(String::new());
        }
        self.phase(Self::working_phase(mode));
        let model = self.client.pick_chat_model(base, &self.settings.chat_model);
        self.client
            .ensure_model(base, &model, &|| self.phase("Downloading the chat model (once)…"))
            .map_err(|e| self.chat_error(e.0, mode))?;
        if cancelled() {
            return Ok(String::new());
        }
        self.phase(Self::working_phase(mode));
        let (system, user, max_tokens) = self.prompts(text, context, mode);
        self.client.chat(base, &model, &system, &user, max_tokens).map_err(|e| self.chat_error(e.0, mode))
    }

    fn chat_error(&self, message: String, mode: SpeakMode) -> String {
        let explain = mode == SpeakMode::Explain;
        let title = match self.settings.chat_backend {
            ChatBackend::Local => llm::failure_title(&message, explain),
            ChatBackend::Lemonade => lemonade::failure_title(&message, explain),
        };
        format!("{title}: {message}")
    }

    /// Reports a download in tens of percent: each phase change is logged and redrawn.
    fn download_phase(&self, what: &str, fraction: f64, last_percent: &mut Option<u32>) {
        let percent = (fraction.clamp(0.0, 1.0) * 10.0).floor() as u32 * 10;
        if *last_percent != Some(percent) {
            *last_percent = Some(percent);
            self.phase(&format!("{what} ({percent}%)…"));
        }
    }

    /// Opens a player session and a thread that reports "Speaking" and the end of playback.
    fn begin_playback(&self) -> Result<Session, String> {
        let session = self.player.begin(&self.settings.audio_device)?;
        let (player, current, generation, emit, received) = (
            self.player.clone(),
            self.current.clone(),
            self.generation,
            self.emit.clone(),
            self.received_audio.clone(),
        );
        std::thread::Builder::new()
            .name("playback-monitor".into())
            .spawn(move || {
                let mut speaking = false;
                while current.load(Ordering::SeqCst) == generation && player.is_current(session) {
                    let (started, drained) = player.progress(session);
                    if started && !speaking {
                        speaking = true;
                        received.store(true, Ordering::SeqCst);
                        emit(Event::Phase(generation, Some("Speaking".into())));
                    }
                    if drained {
                        log!("playback drained");
                        received.store(false, Ordering::SeqCst);
                        emit(Event::Phase(generation, None));
                        return;
                    }
                    std::thread::sleep(Duration::from_millis(15));
                }
            })
            .map_err(|e| e.to_string())?;
        Ok(session)
    }

    /// Feeds one block to the player, waiting while it is far enough ahead. False to stop.
    fn feed(&self, session: Session, pcm: &[f32], pushed: &mut usize) -> bool {
        while self.alive() && self.player.queued_seconds(session) > MAX_QUEUED_SECONDS {
            std::thread::sleep(Duration::from_millis(20));
        }
        if !self.alive() || !self.player.push(session, pcm) {
            return false;
        }
        *pushed += pcm.len();
        true
    }

    fn finish(&self, session: Session, pushed: usize, engine: &str) -> Result<(), String> {
        if !self.alive() {
            return Ok(());
        }
        if pushed == 0 {
            return Err(format!("{engine} returned no audio."));
        }
        self.player.finish(session);
        Ok(())
    }

    fn speak_phonon(&self, chunks: &[String]) -> Result<(), String> {
        let s = &self.settings;
        let cancelled = || !self.alive();
        self.phase(if self.engine.is_loaded() { "Synthesizing…" } else { "Loading Phonon…" });
        let session = self.begin_playback()?;
        let mut pushed = 0usize;
        let mut last_percent = None;
        self.engine
            .speak(
                chunks,
                &s.phonon_voice,
                s.speed,
                &s.phonon_model_dir,
                &s.phonon_language,
                &cancelled,
                &mut |p| match p {
                    phonon::Progress::Downloading(f) => self.download_phase("Downloading Phonon", f, &mut last_percent),
                    phonon::Progress::Loading => self.phase("Loading Phonon…"),
                },
                &mut |pcm| self.feed(session, pcm, &mut pushed),
            )
            .map_err(|e| e.0)?;
        self.finish(session, pushed, "Phonon")
    }
}
