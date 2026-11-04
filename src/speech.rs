//! The speech pipeline: cleanup, optional Summary / Explain / ELI5 through the built-in chat model
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

    /// Speaks `raw` (with optional surrounding text for Explain and ELI5) in `mode`.
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

/// A chat backend ready to answer: the built-in model, or Lemonade with its server up.
enum ChatTarget {
    Local(&'static llm::ChatModel),
    Lemonade { base: String, model: String },
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
        if mode.uses_chat() {
            let started = Instant::now();
            let backend = self.prepare_chat(mode)?;
            if cancelled() {
                return Ok(());
            }
            // ELI5's first step: what a short highlight is in the passage around it.
            let meaning = match context.as_deref() {
                Some(c) if mode == SpeakMode::Eli5 && text::is_short_selection(text) => {
                    self.eli5_meaning(&backend, text, c)
                }
                _ => None,
            };
            if cancelled() {
                return Ok(());
            }
            self.phase(Self::working_phase(mode));
            let (system, user, max_tokens) = self.prompts(text, context.as_deref(), meaning.as_deref(), mode);
            let reply = self.ask(&backend, llm::Request::new(&system, &user, max_tokens), mode)?;
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
            if mode == SpeakMode::Eli5 {
                spoken = text::first_sentences(&spoken, text::ELI5_MAX_SENTENCES);
            }
            if spoken.is_empty() {
                return Err(if mode == SpeakMode::Summary {
                    "The summary was empty."
                } else {
                    "The explanation was empty."
                }
                .into());
            }
        }
        if cancelled() {
            return Ok(());
        }

        let chunks = text::chunks(&spoken);
        self.speak_phonon(&chunks)
    }

    /// The system prompt, user message and reply limit for a chat request in `mode`.
    fn prompts(
        &self,
        text: &str,
        context: Option<&str>,
        meaning: Option<&str>,
        mode: SpeakMode,
    ) -> (String, String, u32) {
        let s = &self.settings;
        match mode {
            SpeakMode::Explain => {
                let n = s.explain_sentence_count;
                (
                    text::explain_prompt(s.explain_audience, n),
                    text::explain_user_message(text, context),
                    text::explain_max_tokens(n),
                )
            }
            SpeakMode::Eli5 => {
                (text::ELI5_PROMPT.to_string(), text::eli5_user_message(text, context, meaning), text::ELI5_MAX_TOKENS)
            }
            _ => (text::SUMMARY_PROMPT.to_string(), text.to_string(), text::SUMMARY_MAX_TOKENS),
        }
    }

    fn working_phase(mode: SpeakMode) -> &'static str {
        match mode {
            SpeakMode::Explain => "Explaining…",
            SpeakMode::Eli5 => "Simplifying…",
            _ => "Summarizing…",
        }
    }

    /// Gets the chosen chat backend ready for `mode`: the built-in model is loaded on first use,
    /// while Lemonade's server and model are started and fetched here.
    fn prepare_chat(&self, mode: SpeakMode) -> Result<ChatTarget, String> {
        match self.settings.chat_backend {
            ChatBackend::Local => {
                let model = llm::find_model(&self.settings.local_model);
                if !self.llm.is_loaded() && llm::is_downloaded(model) {
                    self.phase("Loading the chat model…");
                }
                Ok(ChatTarget::Local(model))
            }
            ChatBackend::Lemonade => {
                let base = self.settings.base_url.clone();
                let cancelled = || !self.alive();
                if !self.client.is_healthy(&base) {
                    self.phase("Starting Lemonade…");
                }
                self.client.ensure_server(&base, &cancelled).map_err(|e| self.chat_error(e.0, mode))?;
                if cancelled() {
                    return Ok(ChatTarget::Lemonade { base, model: String::new() });
                }
                self.phase(Self::working_phase(mode));
                let model = self.client.pick_chat_model(&base, &self.settings.chat_model);
                self.client
                    .ensure_model(&base, &model, &|| self.phase("Downloading the chat model (once)…"))
                    .map_err(|e| self.chat_error(e.0, mode))?;
                Ok(ChatTarget::Lemonade { base, model })
            }
        }
    }

    /// One chat request on a prepared backend. A cancelled request returns an empty reply.
    fn ask(&self, target: &ChatTarget, req: llm::Request, mode: SpeakMode) -> Result<String, String> {
        let cancelled = || !self.alive();
        match target {
            ChatTarget::Local(model) => {
                let mut last_percent = None;
                self.llm
                    .request(model.id, &req, &cancelled, &mut |p| match p {
                        llm::Progress::Downloading(f) => {
                            self.download_phase("Downloading the chat model", f, &mut last_percent);
                        }
                        llm::Progress::Loading => self.phase("Loading the chat model…"),
                        llm::Progress::Writing => self.phase(Self::working_phase(mode)),
                    })
                    .map_err(|e| self.chat_error(e.0, mode))
            }
            ChatTarget::Lemonade { base, model } => {
                if cancelled() {
                    return Ok(String::new());
                }
                self.client
                    .chat_turns(base, model, req.system, req.shots, req.user, Some(req.max_tokens), req.temperature)
                    .map_err(|e| self.chat_error(e.0, mode))
            }
        }
    }

    /// ELI5's first step: what the highlight is in the passage. On failure ELI5 goes on without it.
    fn eli5_meaning(&self, target: &ChatTarget, selection: &str, context: &str) -> Option<String> {
        let started = Instant::now();
        let user = text::eli5_meaning_message(selection, context);
        let req = llm::Request {
            system: text::ELI5_MEANING_PROMPT,
            shots: &text::ELI5_MEANING_SHOTS,
            user: &user,
            max_tokens: text::ELI5_MEANING_MAX_TOKENS,
            temperature: Some(text::ELI5_MEANING_TEMPERATURE),
        };
        let meaning = self.ask(target, req, SpeakMode::Eli5).ok().as_deref().and_then(text::parse_meaning);
        // Timings only; the log never records what was read.
        log!(
            "eli5 meaning: {} in {} ms",
            if meaning.is_some() { "found" } else { "none" },
            started.elapsed().as_millis()
        );
        meaning
    }

    fn chat_error(&self, message: String, mode: SpeakMode) -> String {
        let title = match self.settings.chat_backend {
            ChatBackend::Local => llm::failure_title(&message, mode.title()),
            ChatBackend::Lemonade => lemonade::failure_title(&message, mode.title()),
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

#[cfg(test)]
mod tests {
    use super::*;

    const ELI5_CASES: [(&str, &str); 4] = [
        (
            "Lemonade",
            "Lemonade was already running from the earlier install (port 13305) with Llama-3.2-1B-Instruct-GGUF loaded.",
        ),
        ("lemonade", "On hot days the kids sold lemonade from a stand at the end of the road."),
        ("Kafka", "Kafka buffers the events until the workers catch up."),
        ("Kafka", "Kafka wrote The Metamorphosis, about a man who wakes up as an insect."),
    ];

    /// Runs ELI5 on the built-in chat model, if it has been downloaded, and prints each meaning
    /// and explanation. Run with `cargo test eli5_on_local_model -- --ignored --nocapture`.
    #[test]
    #[ignore]
    #[cfg(feature = "local-llm")]
    fn eli5_on_local_model() {
        let model = llm::find_model(&Settings::default().local_model);
        if !llm::is_downloaded(model) {
            eprintln!("{} not downloaded; skipping", model.id);
            return;
        }
        let engine = llm::Engine::default();
        let ask = |req: &llm::Request| engine.request(model.id, req, &|| false, &mut |_| {}).unwrap();
        for (selection, context) in ELI5_CASES {
            let started = std::time::Instant::now();
            let question = text::eli5_meaning_message(selection, context);
            let meaning = text::parse_meaning(&ask(&llm::Request {
                system: text::ELI5_MEANING_PROMPT,
                shots: &text::ELI5_MEANING_SHOTS,
                user: &question,
                max_tokens: text::ELI5_MEANING_MAX_TOKENS,
                temperature: Some(text::ELI5_MEANING_TEMPERATURE),
            }));
            let message = text::eli5_user_message(selection, Some(context), meaning.as_deref());
            let reply = ask(&llm::Request::new(text::ELI5_PROMPT, &message, text::ELI5_MAX_TOKENS));
            let spoken = text::first_sentences(&text::sanitize(&reply), text::ELI5_MAX_SENTENCES);
            println!("{selection} [{}] in {:?}\n    {spoken:?}\n", meaning.unwrap_or_default(), started.elapsed());
            assert!(!spoken.is_empty() && !spoken.contains("<think>"));
        }
    }

    /// Runs ELI5 against a local Lemonade with the default chat model and prints each meaning
    /// and explanation, to check the prompts by ear. Run with
    /// `cargo test eli5_against_lemonade -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn eli5_against_lemonade() {
        let (client, base) = (lemonade::Client::new(), Settings::default().base_url);
        let model = Settings::default().chat_model;
        let cases = ELI5_CASES;
        for (selection, context) in cases {
            for _ in 0..3 {
                let meaning = client
                    .chat_turns(
                        &base,
                        &model,
                        text::ELI5_MEANING_PROMPT,
                        &text::ELI5_MEANING_SHOTS,
                        &text::eli5_meaning_message(selection, context),
                        Some(text::ELI5_MEANING_MAX_TOKENS),
                        Some(text::ELI5_MEANING_TEMPERATURE),
                    )
                    .ok()
                    .as_deref()
                    .and_then(text::parse_meaning);
                let message = text::eli5_user_message(selection, Some(context), meaning.as_deref());
                let reply = client
                    .chat_turns(&base, &model, text::ELI5_PROMPT, &[], &message, Some(text::ELI5_MAX_TOKENS), None)
                    .unwrap();
                let spoken = text::first_sentences(&text::sanitize(&reply), text::ELI5_MAX_SENTENCES);
                println!("{selection} [{}]\n    {spoken:?}\n", meaning.unwrap_or_default());
            }
        }
    }
}
