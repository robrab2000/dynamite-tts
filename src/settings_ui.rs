//! The Settings window. It runs as its own process (`dynamite-tts --settings-window`) so the
//! tray app stays small: changes are saved to settings.json as they are made and the running
//! app is told to `reload`. "Test voice" asks the app to speak a sample.

use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

use eframe::egui;

use crate::hotkey::{Hotkey, canonical_key};
use crate::settings::{Engine, ExplainAudience, KOKORO_VOICES, PHONON_LANGUAGES, Settings, SpeakMode, Store};
use crate::{audio, autostart, control, lemonade, paths, voices};

const SAMPLE: &str = "Hello! This is how Dynamite TTS sounds with these settings.";

fn raise_file() -> PathBuf {
    paths::data_dir().join("raise-settings")
}

/// Asks an open Settings window to come to the front.
pub fn raise() -> std::io::Result<()> {
    std::fs::write(raise_file(), b"")
}

pub fn run() -> i32 {
    // One Settings window: a second launch just raises the first.
    let lock_path = paths::data_dir().join("settings-window.lock");
    let _ = std::fs::create_dir_all(paths::data_dir());
    let Ok(lock) = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(&lock_path) else {
        return 1;
    };
    if lock.try_lock().is_err() {
        let _ = raise();
        return 0;
    }
    let icon = crate::tray::decode(include_bytes!("../assets/icons/idle.png"))
        .map(|(rgba, width, height)| egui::IconData { rgba, width, height });
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("Dynamite TTS Settings")
        .with_inner_size([560.0, 720.0])
        .with_min_inner_size([460.0, 420.0]);
    if let Some(icon) = icon {
        viewport = viewport.with_icon(icon);
    }
    let options = eframe::NativeOptions { viewport, centered: true, ..Default::default() };
    let result = eframe::run_native(
        "Dynamite TTS Settings",
        options,
        Box::new(|_cc| Ok(Box::new(SettingsApp::new()) as Box<dyn eframe::App>)),
    );
    drop(lock);
    match result {
        Ok(()) => 0,
        Err(e) => {
            eprintln!("settings window failed: {e}");
            1
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Recording {
    Speak,
    Mode,
}

struct Shared {
    status: String,
    models: Vec<String>,
    models_note: String,
}

struct SettingsApp {
    store: Store,
    settings: Settings,
    saved: Settings,
    dirty_since: Option<Instant>,
    recording: Option<Recording>,
    hotkey_error: String,
    devices: Vec<String>,
    shared: Arc<Mutex<Shared>>,
    last_ping: Instant,
    raise_seen: Option<SystemTime>,
}

impl SettingsApp {
    fn new() -> SettingsApp {
        let store = Store::default_location();
        let mut settings = store.load();
        // Show the login item as it really is (the installer may have made one).
        settings.launch_at_login = autostart::is_enabled();
        let raise_seen = std::fs::metadata(raise_file()).and_then(|m| m.modified()).ok();
        let app = SettingsApp {
            saved: settings.clone(),
            settings,
            store,
            dirty_since: None,
            recording: None,
            hotkey_error: String::new(),
            devices: audio::output_devices(),
            shared: Arc::new(Mutex::new(Shared {
                status: "Checking…".into(),
                models: Vec::new(),
                models_note: String::new(),
            })),
            last_ping: Instant::now() - Duration::from_secs(10),
            raise_seen,
        };
        app.refresh_models();
        app
    }

    fn shared(&self) -> std::sync::MutexGuard<'_, Shared> {
        self.shared.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn refresh_models(&self) {
        let base = self.settings.base_url.clone();
        let shared = self.shared.clone();
        std::thread::spawn(move || {
            let client = lemonade::Client::new();
            let (models, note) = if client.is_healthy(&base) {
                let ids: Vec<String> =
                    client.list_models(&base).into_iter().filter(|m| !m.contains("kokoro")).collect();
                let note = if ids.is_empty() { "Lemonade has no models yet.".to_string() } else { String::new() };
                (ids, note)
            } else {
                (Vec::new(), "Lemonade is not running; it starts the first time Summary or Explain is used.".into())
            };
            let mut s = shared.lock().unwrap_or_else(|e| e.into_inner());
            s.models = models;
            s.models_note = note;
        });
    }

    fn ping(&mut self) {
        if self.last_ping.elapsed() < Duration::from_millis(1000) {
            return;
        }
        self.last_ping = Instant::now();
        let shared = self.shared.clone();
        std::thread::spawn(move || {
            let status = match control::send("ping") {
                Some(reply) => reply
                    .split_whitespace()
                    .find_map(|w| w.strip_prefix("phase="))
                    .map(|p| format!("Dynamite TTS is running · {p}"))
                    .unwrap_or_else(|| "Dynamite TTS is running".into()),
                None => "Dynamite TTS is not running; changes apply the next time it starts.".into(),
            };
            shared.lock().unwrap_or_else(|e| e.into_inner()).status = status;
        });
    }

    fn save_if_due(&mut self) {
        let Some(since) = self.dirty_since else { return };
        if since.elapsed() < Duration::from_millis(350) {
            return;
        }
        self.dirty_since = None;
        let normalized = self.settings.clone().normalized();
        if normalized == self.saved {
            return;
        }
        match self.store.save(&normalized) {
            Ok(()) => {
                self.saved = normalized;
                let _ = control::send("reload");
            }
            Err(e) => self.shared().status = format!("Could not save settings: {e}"),
        }
    }

    fn check_raise(&mut self, ctx: &egui::Context) {
        let modified = std::fs::metadata(raise_file()).and_then(|m| m.modified()).ok();
        if modified.is_some() && modified != self.raise_seen {
            self.raise_seen = modified;
            ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
        }
    }

    fn record_hotkey(&mut self, ctx: &egui::Context) {
        let Some(target) = self.recording else { return };
        let events = ctx.input(|i| i.events.clone());
        for event in events {
            let egui::Event::Key { key, pressed: true, modifiers, .. } = event else { continue };
            if key == egui::Key::Escape && !modifiers.any() {
                self.finish_recording();
                return;
            }
            let Some(name) = canonical_key(key.name()) else { continue };
            let hk = Hotkey {
                ctrl: modifiers.ctrl,
                alt: modifiers.alt,
                shift: modifiers.shift,
                meta: modifiers.mac_cmd,
                key: name,
            };
            if !hk.has_modifier() {
                self.hotkey_error = "Use at least one modifier (Ctrl, Alt, Shift…).".into();
                continue;
            }
            let other = match target {
                Recording::Speak => &self.settings.mode_hotkey,
                Recording::Mode => &self.settings.speak_hotkey,
            };
            if Hotkey::parse(other).as_ref() == Some(&hk) {
                self.hotkey_error = format!("{} is already the other shortcut.", hk.display());
                continue;
            }
            match target {
                Recording::Speak => self.settings.speak_hotkey = hk.to_storage(),
                Recording::Mode => self.settings.mode_hotkey = hk.to_storage(),
            }
            self.hotkey_error.clear();
            self.dirty_since = Some(Instant::now() - Duration::from_secs(1));
            self.finish_recording();
            return;
        }
    }

    fn start_recording(&mut self, which: Recording) {
        self.recording = Some(which);
        self.hotkey_error.clear();
        // Free the keys so the window, not the app, sees the shortcut being pressed.
        let _ = control::send("suspend-hotkeys");
    }

    fn finish_recording(&mut self) {
        self.recording = None;
        let _ = control::send("resume-hotkeys");
    }

    fn hotkey_row(&mut self, ui: &mut egui::Ui, label: &str, which: Recording) {
        ui.label(label);
        let current = match which {
            Recording::Speak => &self.settings.speak_hotkey,
            Recording::Mode => &self.settings.mode_hotkey,
        };
        let text = if self.recording == Some(which) {
            "Press the new shortcut… (Esc cancels)".to_string()
        } else {
            Hotkey::parse(current).map(|h| h.display()).unwrap_or_else(|| current.clone())
        };
        if ui.button(text).clicked() {
            if self.recording == Some(which) {
                self.finish_recording();
            } else {
                self.start_recording(which);
            }
        }
        ui.end_row();
    }

    fn body(&mut self, ui: &mut egui::Ui) {
        let before = self.settings.clone();
        let status = self.shared().status.clone();
        ui.label(egui::RichText::new(status).weak());
        ui.add_space(6.0);

        section(ui, "Speech");
        egui::Grid::new("speech").num_columns(2).spacing([12.0, 8.0]).show(ui, |ui| {
            ui.label("Engine");
            ui.horizontal(|ui| {
                ui.radio_value(&mut self.settings.engine, Engine::Phonon, Engine::Phonon.title());
                ui.radio_value(&mut self.settings.engine, Engine::Kokoro, Engine::Kokoro.title());
            });
            ui.end_row();

            if self.settings.engine == Engine::Phonon {
                ui.label("Language");
                let lang_label = PHONON_LANGUAGES
                    .iter()
                    .find(|(id, _)| *id == self.settings.phonon_language)
                    .map_or(self.settings.phonon_language.clone(), |(_, l)| l.to_string());
                egui::ComboBox::from_id_salt("lang").width(300.0).selected_text(lang_label).show_ui(ui, |ui| {
                    for (id, label) in PHONON_LANGUAGES {
                        ui.selectable_value(&mut self.settings.phonon_language, id.to_string(), *label);
                    }
                });
                ui.end_row();

                ui.label("Voice");
                let voice_label =
                    voices::find(&self.settings.phonon_voice).map_or(self.settings.phonon_voice.clone(), |v| v.label());
                egui::ComboBox::from_id_salt("phonon_voice").width(300.0).selected_text(voice_label).show_ui(
                    ui,
                    |ui| {
                        for v in voices::for_language(&self.settings.phonon_language) {
                            let id = if v.is_default() { voices::DEFAULT_ID.to_string() } else { v.name.clone() };
                            ui.selectable_value(&mut self.settings.phonon_voice, id, v.label());
                        }
                    },
                );
                ui.end_row();

                ui.label("Model folder");
                ui.add(
                    egui::TextEdit::singleline(&mut self.settings.phonon_model_dir)
                        .hint_text("empty = Phonon 31M English (bundled or downloaded once)")
                        .desired_width(300.0),
                );
                ui.end_row();
            } else {
                ui.label("Voice");
                let label = KOKORO_VOICES
                    .iter()
                    .find(|(id, _)| *id == self.settings.voice)
                    .map_or(self.settings.voice.clone(), |(_, l)| l.to_string());
                egui::ComboBox::from_id_salt("kokoro_voice").width(300.0).selected_text(label).show_ui(ui, |ui| {
                    for (id, label) in KOKORO_VOICES {
                        ui.selectable_value(&mut self.settings.voice, id.to_string(), *label);
                    }
                });
                ui.end_row();
            }

            ui.label("Speed");
            ui.add(egui::Slider::new(&mut self.settings.speed, 0.5..=2.0).step_by(0.05).suffix("x"));
            ui.end_row();

            ui.label("Output");
            let device_label = if self.settings.audio_device.is_empty() {
                "System default".to_string()
            } else {
                self.settings.audio_device.clone()
            };
            ui.horizontal(|ui| {
                egui::ComboBox::from_id_salt("device").width(260.0).selected_text(device_label).show_ui(ui, |ui| {
                    ui.selectable_value(&mut self.settings.audio_device, String::new(), "System default");
                    for d in &self.devices {
                        ui.selectable_value(&mut self.settings.audio_device, d.clone(), d);
                    }
                });
                if ui.small_button("↻").on_hover_text("Refresh the device list").clicked() {
                    self.devices = audio::output_devices();
                }
            });
            ui.end_row();
        });
        if self.settings.engine == Engine::Phonon && !(0.67..=1.5).contains(&self.settings.speed) {
            ui.label(egui::RichText::new("Phonon speaks between about 0.67x and 1.5x.").weak());
        }
        ui.horizontal(|ui| {
            if ui.button("▶ Test voice").clicked() {
                self.save_now();
                if control::send(&format!("speak-text {SAMPLE}")).is_none() {
                    self.shared().status = "Start Dynamite TTS to test the voice.".into();
                }
            }
            if ui.button("■ Stop").clicked() {
                let _ = control::send("stop");
            }
        });

        section(ui, "Modes");
        egui::Grid::new("modes").num_columns(2).spacing([12.0, 8.0]).show(ui, |ui| {
            ui.label("Speak mode");
            ui.horizontal(|ui| {
                for mode in SpeakMode::ALL {
                    ui.radio_value(&mut self.settings.speak_mode, mode, mode.title());
                }
            });
            ui.end_row();

            ui.label("Explain length");
            ui.add(egui::Slider::new(&mut self.settings.explain_sentence_count, 1..=8).suffix(" sentences"));
            ui.end_row();

            ui.label("Explain for");
            ui.horizontal(|ui| {
                for a in [ExplainAudience::Technical, ExplainAudience::General] {
                    ui.radio_value(&mut self.settings.explain_audience, a, a.title());
                }
            });
            ui.end_row();

            ui.label("Chat model");
            let models = self.shared().models.clone();
            ui.horizontal(|ui| {
                egui::ComboBox::from_id_salt("chat")
                    .width(260.0)
                    .selected_text(self.settings.chat_model.clone())
                    .show_ui(ui, |ui| {
                        if !models.iter().any(|m| m.eq_ignore_ascii_case(&self.settings.chat_model)) {
                            let current = self.settings.chat_model.clone();
                            ui.selectable_value(&mut self.settings.chat_model, current.clone(), current);
                        }
                        for m in &models {
                            ui.selectable_value(&mut self.settings.chat_model, m.clone(), m);
                        }
                    });
                if ui.small_button("↻").on_hover_text("Ask Lemonade for its models").clicked() {
                    self.refresh_models();
                }
            });
            ui.end_row();

            ui.label("Lemonade address");
            ui.add(egui::TextEdit::singleline(&mut self.settings.base_url).desired_width(260.0));
            ui.end_row();
        });
        let note = self.shared().models_note.clone();
        if !note.is_empty() {
            ui.label(egui::RichText::new(note).weak());
        }

        section(ui, "Shortcuts");
        egui::Grid::new("keys").num_columns(2).spacing([12.0, 8.0]).show(ui, |ui| {
            self.hotkey_row(ui, "Speak selection", Recording::Speak);
            self.hotkey_row(ui, "Change mode", Recording::Mode);
        });
        if !self.hotkey_error.is_empty() {
            ui.colored_label(ui.visuals().warn_fg_color, &self.hotkey_error);
        }
        if cfg!(target_os = "linux") {
            ui.label(
                egui::RichText::new(
                    "On GNOME these become custom shortcuts in Settings → Keyboard. Elsewhere, bind `dynamite-tts speak` and `dynamite-tts toggle-mode` yourself.",
                )
                .weak(),
            );
        }

        section(ui, "Behaviour");
        ui.checkbox(&mut self.settings.hotkey_stops_speech, "Pressing the speak shortcut while speaking stops speech");
        ui.checkbox(&mut self.settings.play_sound_on_stop, "Play a sound when speech is stopped");
        ui.checkbox(
            &mut self.settings.show_status,
            if cfg!(target_os = "linux") { "Show status notifications" } else { "Show the status pill while working" },
        );
        ui.checkbox(&mut self.settings.launch_at_login, "Start Dynamite TTS when I log in");

        section(ui, "Diagnostics");
        ui.horizontal(|ui| {
            ui.label(egui::RichText::new(paths::log_file().display().to_string()).monospace().small());
            if ui.small_button("Open folder").clicked() {
                open_folder(&paths::log_dir());
            }
        });
        ui.label(egui::RichText::new(format!("Version {}", env!("CARGO_PKG_VERSION"))).weak().small());

        if self.settings.launch_at_login != before.launch_at_login {
            if let Err(e) = autostart::apply(self.settings.launch_at_login) {
                self.shared().status = format!("Could not change the login item: {e}");
            }
        }
        if self.settings != before {
            self.dirty_since = Some(Instant::now());
        }
    }

    fn save_now(&mut self) {
        if self.dirty_since.is_some() {
            self.dirty_since = Some(Instant::now() - Duration::from_secs(1));
            self.save_if_due();
        }
    }
}

fn section(ui: &mut egui::Ui, title: &str) {
    ui.add_space(10.0);
    ui.label(egui::RichText::new(title).strong().size(16.0));
    ui.separator();
}

fn open_folder(dir: &std::path::Path) {
    let _ = std::fs::create_dir_all(dir);
    let program = if cfg!(windows) {
        "explorer"
    } else if cfg!(target_os = "macos") {
        "open"
    } else {
        "xdg-open"
    };
    let _ = std::process::Command::new(program).arg(dir).spawn();
}

impl eframe::App for SettingsApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.check_raise(&ctx);
        self.ping();
        self.record_hotkey(&ctx);
        egui::Frame::central_panel(ui.style()).inner_margin(egui::Margin::same(14)).show(ui, |ui| {
            egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| self.body(ui));
        });
        self.save_if_due();
        ctx.request_repaint_after(Duration::from_millis(if self.dirty_since.is_some() { 100 } else { 500 }));
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.save_now();
        if self.recording.is_some() {
            self.finish_recording();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Lays the window out headlessly for both engines: no panics, and every section drawn.
    #[test]
    fn the_window_lays_out() {
        let ctx = egui::Context::default();
        let mut app = SettingsApp::new();
        for engine in [Engine::Phonon, Engine::Kokoro] {
            app.settings.engine = engine;
            let mut texts = Vec::new();
            // Grids size themselves on a first, invisible pass; the second frame is the real one.
            let mut output = None;
            for _ in 0..2 {
                let mut frame = ctx.run_ui(egui::RawInput::default(), |ui| {
                    egui::ScrollArea::vertical().show(ui, |ui| app.body(ui));
                });
                frame.textures_delta.clear();
                output = Some(frame);
            }
            let output = output.unwrap();
            for clipped in &output.shapes {
                if let egui::Shape::Text(t) = &clipped.shape {
                    texts.push(t.galley.text().to_string());
                }
            }
            for wanted in ["Speech", "Modes", "Shortcuts", "Behaviour", "Voice", "Speed"] {
                assert!(texts.iter().any(|t| t == wanted), "{wanted} missing for {engine:?}: {texts:?}");
            }
        }
        // The test must not leave a pending save behind.
        app.dirty_since = None;
    }
}
