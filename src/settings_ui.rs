//! The Settings window. It runs as its own process (`dynamite-tts --settings-window`) so the
//! tray app stays small: changes are saved to settings.json as they are made and the running
//! app is told to `reload`. "Test voice" asks the app to speak a sample.

use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

use eframe::egui::{self, Color32, CornerRadius, Margin, RichText, Stroke, Vec2};

use crate::hotkey::{Hotkey, canonical_key};
use crate::settings::{
    ChatBackend, EqPreset, ExplainAudience, PHONON_LANGUAGES, Settings, SpeakMode, StatusStyle, Store,
};
use crate::{audio, autostart, control, lemonade, llm, paths, voices};

const SAMPLE: &str = "Hello! This is how Dynamite TTS sounds with these settings.";

/// Dynamite orange, used for the selected page, toggles, sliders and the main button.
const ACCENT: Color32 = Color32::from_rgb(0xf2, 0x6b, 0x2a);
const SIDEBAR_WIDTH: f32 = 190.0;
const CONTROL_WIDTH: f32 = 260.0;

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
    let icon = crate::tray::decode(include_bytes!("../assets/icons/app.png"))
        .map(|(rgba, width, height)| egui::IconData { rgba, width, height });
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("Dynamite TTS Settings")
        .with_inner_size([760.0, 660.0])
        .with_min_inner_size([640.0, 440.0]);
    if let Some(icon) = icon {
        viewport = viewport.with_icon(icon);
    }
    let options = eframe::NativeOptions { viewport, centered: true, ..Default::default() };
    let result = eframe::run_native(
        "Dynamite TTS Settings",
        options,
        Box::new(|cc| {
            apply_style(&cc.egui_ctx);
            Ok(Box::new(SettingsApp::new()) as Box<dyn eframe::App>)
        }),
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

/// Roomier spacing, rounder corners, and the accent colour, in both light and dark mode
/// (egui follows the system theme).
fn apply_style(ctx: &egui::Context) {
    ctx.all_styles_mut(|style| {
        style.spacing.item_spacing = Vec2::new(10.0, 10.0);
        style.spacing.button_padding = Vec2::new(12.0, 6.0);
        style.spacing.interact_size.y = 30.0;
        style.spacing.slider_width = CONTROL_WIDTH - 70.0;
        style.spacing.combo_width = CONTROL_WIDTH;
        for (text_style, size) in [
            (egui::TextStyle::Body, 14.0),
            (egui::TextStyle::Button, 14.0),
            (egui::TextStyle::Small, 12.0),
            (egui::TextStyle::Heading, 22.0),
            (egui::TextStyle::Monospace, 13.0),
        ] {
            if let Some(font) = style.text_styles.get_mut(&text_style) {
                font.size = size;
            }
        }
        let v = &mut style.visuals;
        let radius = CornerRadius::same(7);
        for w in [&mut v.widgets.inactive, &mut v.widgets.hovered, &mut v.widgets.active, &mut v.widgets.open] {
            w.corner_radius = radius;
            // egui grows a hovered widget by a pixel; on joined buttons and slider handles that
            // reads as a wobble.
            w.expansion = 0.0;
        }
        v.widgets.noninteractive.corner_radius = radius;
        v.selection.bg_fill = ACCENT;
        v.selection.stroke = Stroke::new(1.5, Color32::WHITE);
        v.slider_trailing_fill = true;
        v.hyperlink_color = ACCENT;
        v.window_corner_radius = CornerRadius::same(10);
        v.menu_corner_radius = CornerRadius::same(8);
    });
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Page {
    Voice,
    Modes,
    Shortcuts,
    General,
}

impl Page {
    const ALL: [Page; 4] = [Page::Voice, Page::Modes, Page::Shortcuts, Page::General];

    fn title(self) -> &'static str {
        match self {
            Page::Voice => "Voice",
            Page::Modes => "Modes",
            Page::Shortcuts => "Shortcuts",
            Page::General => "General",
        }
    }

    fn subtitle(self) -> &'static str {
        match self {
            Page::Voice => "How the highlight is read aloud. Everything runs on this computer.",
            Page::Modes => {
                "Read the highlight as it is, summarize it, or explain it first, plainly or like you're five."
            }
            Page::Shortcuts => "Keys that work from any app.",
            Page::General => "Behaviour, start-up and diagnostics.",
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Recording {
    Speak,
    Mode,
}

struct Shared {
    message: String,
    models: Vec<String>,
    models_note: String,
}

struct SettingsApp {
    store: Store,
    settings: Settings,
    saved: Settings,
    dirty_since: Option<Instant>,
    page: Page,
    recording: Option<Recording>,
    hotkey_error: String,
    devices: Vec<String>,
    shared: Arc<Mutex<Shared>>,
    raise_seen: Option<SystemTime>,
    /// The EQ gains last sent live to the app, and when.
    eq_sent: [f32; 3],
    eq_sent_at: Instant,
    icon: Option<egui::TextureHandle>,
}

impl SettingsApp {
    fn new() -> SettingsApp {
        let store = Store::default_location();
        let mut settings = store.load();
        // Show the login item as it really is (the installer may have made one).
        settings.launch_at_login = autostart::is_enabled();
        let raise_seen = std::fs::metadata(raise_file()).and_then(|m| m.modified()).ok();
        let eq_sent = [settings.eq_low_db, settings.eq_mid_db, settings.eq_high_db];
        let app = SettingsApp {
            saved: settings.clone(),
            settings,
            store,
            dirty_since: None,
            page: Page::Voice,
            recording: None,
            hotkey_error: String::new(),
            devices: audio::output_devices(),
            shared: Arc::new(Mutex::new(Shared {
                message: String::new(),
                models: Vec::new(),
                models_note: String::new(),
            })),
            raise_seen,
            eq_sent,
            eq_sent_at: Instant::now(),
            icon: None,
        };
        if app.settings.chat_backend == ChatBackend::Lemonade {
            app.refresh_models();
        }
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
                (
                    Vec::new(),
                    "Lemonade isn't running. It starts the first time Summary, Explain or ELI5 is used.".into(),
                )
            };
            let mut s = shared.lock().unwrap_or_else(|e| e.into_inner());
            s.models = models;
            s.models_note = note;
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
            Err(e) => self.shared().message = format!("Could not save settings: {e}"),
        }
    }

    fn save_now(&mut self) {
        if self.dirty_since.is_some() {
            self.dirty_since = Some(Instant::now() - Duration::from_secs(1));
            self.save_if_due();
        }
    }

    /// While a tone slider moves, tells the app the new gains straight away (at most every 40 ms)
    /// so the voice that is speaking changes with it; saving follows on the usual debounce.
    fn send_eq_live(&mut self) {
        let s = &self.settings;
        let gains = [s.eq_low_db, s.eq_mid_db, s.eq_high_db];
        if gains == self.eq_sent || self.eq_sent_at.elapsed() < Duration::from_millis(40) {
            return;
        }
        self.eq_sent = gains;
        self.eq_sent_at = Instant::now();
        let [low, mid, high] = gains;
        std::thread::spawn(move || {
            let _ = control::send(&format!("set-eq {low} {mid} {high}"));
        });
    }

    fn check_raise(&mut self, ctx: &egui::Context) {
        let modified = std::fs::metadata(raise_file()).and_then(|m| m.modified()).ok();
        if modified.is_some() && modified != self.raise_seen {
            self.raise_seen = modified;
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
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

    // ---------------------------------------------------------------- layout

    fn sidebar(&mut self, ui: &mut egui::Ui) {
        ui.add_space(18.0);
        ui.horizontal(|ui| {
            ui.add_space(6.0);
            if self.icon.is_none() {
                self.icon = crate::tray::decode(include_bytes!("../assets/icons/speaking.png")).map(|(rgba, w, h)| {
                    let image = egui::ColorImage::from_rgba_unmultiplied([w as usize, h as usize], &rgba);
                    ui.ctx().load_texture("app-icon", image, egui::TextureOptions::LINEAR)
                });
            }
            if let Some(icon) = &self.icon {
                ui.add(egui::Image::new((icon.id(), Vec2::splat(34.0))));
            }
            ui.vertical(|ui| {
                ui.spacing_mut().item_spacing.y = 0.0;
                ui.label(RichText::new("Dynamite TTS").strong().size(16.0));
                ui.label(RichText::new(format!("Version {}", env!("CARGO_PKG_VERSION"))).small().weak());
            });
        });
        ui.add_space(18.0);
        for page in Page::ALL {
            if nav_item(ui, page, self.page == page).clicked() {
                self.page = page;
            }
        }
    }

    fn content(&mut self, ui: &mut egui::Ui) {
        let before = self.settings.clone();
        ui.add_space(6.0);
        ui.heading(RichText::new(self.page.title()).strong());
        ui.label(RichText::new(self.page.subtitle()).weak());
        let message = self.shared().message.clone();
        if !message.is_empty() {
            ui.colored_label(ui.visuals().warn_fg_color, message);
        }
        ui.add_space(8.0);
        match self.page {
            Page::Voice => self.voice_page(ui),
            Page::Modes => self.modes_page(ui),
            Page::Shortcuts => self.shortcuts_page(ui),
            Page::General => self.general_page(ui),
        }
        ui.add_space(12.0);
        if self.settings.launch_at_login != before.launch_at_login {
            if let Err(e) = autostart::apply(self.settings.launch_at_login) {
                self.shared().message = format!("Could not change the login item: {e}");
            }
        }
        if self.settings != before {
            self.dirty_since = Some(Instant::now());
        }
    }

    fn voice_page(&mut self, ui: &mut egui::Ui) {
        card(ui, |ui| {
            row(ui, "Voice", "About 100 voices; each downloads the first time you use it.", |ui| {
                let label =
                    voices::find(&self.settings.phonon_voice).map_or(self.settings.phonon_voice.clone(), |v| v.label());
                egui::ComboBox::from_id_salt("phonon_voice").selected_text(label).height(360.0).show_ui(ui, |ui| {
                    for v in voices::for_language(&self.settings.phonon_language) {
                        let id = if v.is_default() { voices::DEFAULT_ID.to_string() } else { v.name.clone() };
                        ui.selectable_value(&mut self.settings.phonon_voice, id, v.label());
                    }
                });
            });
            divider(ui);
            row(ui, "Speed", "Phonon speaks between about 0.67x and 1.5x.", |ui| {
                ui.add(
                    egui::Slider::new(&mut self.settings.speed, 0.67..=1.5).step_by(0.01).fixed_decimals(2).suffix("×"),
                );
            });
            divider(ui);
            row(ui, "Language", "How numbers and abbreviations are read.", |ui| {
                let label = PHONON_LANGUAGES
                    .iter()
                    .find(|(id, _)| *id == self.settings.phonon_language)
                    .map_or(self.settings.phonon_language.clone(), |(_, l)| l.to_string());
                egui::ComboBox::from_id_salt("lang").selected_text(label).show_ui(ui, |ui| {
                    for (id, label) in PHONON_LANGUAGES {
                        ui.selectable_value(&mut self.settings.phonon_language, id.to_string(), *label);
                    }
                });
            });
        });
        ui.add_space(8.0);
        section_label(ui, "Tone");
        self.tone_card(ui);
        ui.add_space(6.0);
        ui.horizontal(|ui| {
            if ui.add(accent_button("Test voice")).clicked() {
                self.save_now();
                if control::send(&format!("speak-text {SAMPLE}")).is_none() {
                    self.shared().message = "Start Dynamite TTS to test the voice.".into();
                }
            }
            if ui.button("Stop").clicked() {
                let _ = control::send("stop");
            }
        });
        ui.add_space(8.0);
        card(ui, |ui| {
            row(ui, "Output device", "Where speech plays.", |ui| {
                ui.horizontal(|ui| {
                    if ui.small_button("Refresh").on_hover_text("Refresh the device list").clicked() {
                        self.devices = audio::output_devices();
                    }
                    let device = &self.settings.audio_device;
                    let label = if device.is_empty() {
                        "System default".to_string()
                    } else if self.devices.iter().any(|d| d == device) {
                        device.clone()
                    } else {
                        // Unplugged, or saved on another machine: speech uses the default for now.
                        format!("System default ({device} not found)")
                    };
                    egui::ComboBox::from_id_salt("device").width(CONTROL_WIDTH - 40.0).selected_text(label).show_ui(
                        ui,
                        |ui| {
                            ui.selectable_value(&mut self.settings.audio_device, String::new(), "System default");
                            for d in &self.devices {
                                ui.selectable_value(&mut self.settings.audio_device, d.clone(), d);
                            }
                        },
                    );
                });
            });
            divider(ui);
            self.model_folder_row(ui);
        });
    }

    /// The Phonon checkpoint folder: built-in by default, or one picked with the system folder dialog
    /// (for the gated multilingual 40M checkpoints).
    fn model_folder_row(&mut self, ui: &mut egui::Ui) {
        let dir = self.settings.phonon_model_dir.trim().to_string();
        let (hint, valid) = if dir.is_empty() {
            ("Built-in Phonon 31M English. Choose a folder to use a Phonon 40M checkpoint instead.".to_string(), true)
        } else {
            let path = std::path::Path::new(&dir);
            let name = path.file_name().map_or(dir.clone(), |n| n.to_string_lossy().into_owned());
            (format!("Using {name}."), crate::phonon::is_checkpoint(path))
        };
        row(ui, "Model folder", &hint, |ui| {
            if ui.button("Choose…").clicked() {
                let mut dialog = rfd::FileDialog::new().set_title("Choose a Phonon model folder");
                if !dir.is_empty() {
                    dialog = dialog.set_directory(&dir);
                }
                if let Some(picked) = dialog.pick_folder() {
                    self.settings.phonon_model_dir = picked.display().to_string();
                }
            }
            if !dir.is_empty() && ui.button("Use built-in").clicked() {
                self.settings.phonon_model_dir.clear();
            }
        });
        if !dir.is_empty() {
            ui.label(RichText::new(&dir).monospace().small().weak());
            if !valid {
                ui.colored_label(
                    ui.visuals().warn_fg_color,
                    RichText::new("This folder has no Phonon checkpoint: it needs config.json, tokenizer.json and model.q8.gguf or model.safetensors. Speech will fail until you choose a valid folder or switch back to the built-in model.").small(),
                );
            }
        }
    }

    /// Three-band EQ: a preset row, then Low / Mid / High sliders. Moving a slider makes it Custom.
    fn tone_card(&mut self, ui: &mut egui::Ui) {
        card(ui, |ui| {
            let before = self.settings.eq_preset;
            segmented(ui, &mut self.settings.eq_preset, &EqPreset::ALL.map(|p| (p, p.title())));
            if self.settings.eq_preset != before {
                if let Some([low, mid, high]) = self.settings.eq_preset.gains() {
                    (self.settings.eq_low_db, self.settings.eq_mid_db, self.settings.eq_high_db) = (low, mid, high);
                }
            }
            ui.label(RichText::new(eq_preset_hint(self.settings.eq_preset)).small().weak());
            divider(ui);
            let bands = [
                ("Low", "Body and warmth, around 220 Hz.", 0),
                ("Mid", "Presence and clarity, around 1.8 kHz.", 1),
                ("High", "Air and sibilance, above 5 kHz.", 2),
            ];
            for (i, (title, hint, band)) in bands.into_iter().enumerate() {
                if i > 0 {
                    divider(ui);
                }
                let mut moved = false;
                let gain = match band {
                    0 => &mut self.settings.eq_low_db,
                    1 => &mut self.settings.eq_mid_db,
                    _ => &mut self.settings.eq_high_db,
                };
                row(ui, title, hint, |ui| {
                    let max = crate::eq::MAX_DB;
                    let slider = egui::Slider::new(gain, -max..=max)
                        .step_by(0.5)
                        .fixed_decimals(1)
                        .suffix(" dB")
                        .trailing_fill(false);
                    moved = ui.add(slider).changed();
                });
                if moved {
                    self.settings.eq_preset = EqPreset::Custom;
                }
            }
        });
    }

    fn modes_page(&mut self, ui: &mut egui::Ui) {
        card(ui, |ui| {
            let hint = format!(
                "{} cycles through these from any app.",
                Hotkey::parse(&self.settings.mode_hotkey)
                    .map_or_else(|| self.settings.mode_hotkey.clone(), |h| h.display())
            );
            row(ui, "Speak mode", &hint, |ui| {
                segmented(ui, &mut self.settings.speak_mode, &SpeakMode::ALL.map(|m| (m, m.title())));
            });
        });
        ui.add_space(8.0);
        section_label(ui, "Explain");
        card(ui, |ui| {
            row(ui, "Length", "Roughly how long an explanation runs.", |ui| {
                ui.add(egui::Slider::new(&mut self.settings.explain_sentence_count, 1..=8).suffix(" sentences"));
            });
            divider(ui);
            row(ui, "Written for", "Technical keeps the jargon; General avoids it.", |ui| {
                segmented(
                    ui,
                    &mut self.settings.explain_audience,
                    &[ExplainAudience::Technical, ExplainAudience::General].map(|a| (a, a.title())),
                );
            });
        });
        ui.add_space(8.0);
        section_label(ui, "Summary, Explain and ELI5");
        card(ui, |ui| {
            let before = self.settings.chat_backend;
            row(
                ui,
                "Chat model",
                "Built in runs a small model inside the app. Lemonade uses AMD's server, for Ryzen AI NPUs.",
                |ui| segmented(ui, &mut self.settings.chat_backend, &ChatBackend::ALL.map(|b| (b, b.title()))),
            );
            if before != self.settings.chat_backend && self.settings.chat_backend == ChatBackend::Lemonade {
                self.refresh_models();
            }
            divider(ui);
            match self.settings.chat_backend {
                ChatBackend::Local => self.local_model_row(ui),
                ChatBackend::Lemonade => self.lemonade_rows(ui),
            }
        });
        let note = match self.settings.chat_backend {
            ChatBackend::Local => format!(
                "Qwen3 by Alibaba Cloud (Apache 2.0), run with llama.cpp. The model downloads the first time \
Summary, Explain or ELI5 is used, and is freed after {} minutes unused.",
                llm::IDLE_UNLOAD.as_secs() / 60
            ),
            ChatBackend::Lemonade => self.shared().models_note.clone(),
        };
        if !note.is_empty() {
            ui.add(egui::Label::new(RichText::new(note).small().weak()).wrap());
        }
    }

    fn local_model_row(&mut self, ui: &mut egui::Ui) {
        let current = llm::find_model(&self.settings.local_model);
        let hint = format!(
            "{} {} download{}. Up to {} of memory while loaded.",
            current.note,
            llm::size_label(current.file.size),
            if llm::is_downloaded(current) { ", done" } else { "" },
            llm::size_label(current.memory_estimate()),
        );
        row(ui, "Model", &hint, |ui| {
            egui::ComboBox::from_id_salt("local_model")
                .width(CONTROL_WIDTH)
                .selected_text(model_choice(current))
                .show_ui(ui, |ui| {
                    for m in &llm::CHAT_MODELS {
                        ui.selectable_value(&mut self.settings.local_model, m.id.to_string(), model_choice(m));
                    }
                });
        });
    }

    fn lemonade_rows(&mut self, ui: &mut egui::Ui) {
        let models = self.shared().models.clone();
        row(ui, "Lemonade model", "The model Lemonade uses for Summary, Explain and ELI5.", |ui| {
            ui.horizontal(|ui| {
                if ui.small_button("Refresh").on_hover_text("Ask Lemonade for its models").clicked() {
                    self.refresh_models();
                }
                egui::ComboBox::from_id_salt("chat")
                    .width(CONTROL_WIDTH - 40.0)
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
            });
        });
        divider(ui);
        row(ui, "Server address", "Where Lemonade listens.", |ui| {
            ui.add(egui::TextEdit::singleline(&mut self.settings.base_url).desired_width(CONTROL_WIDTH));
        });
    }

    fn shortcuts_page(&mut self, ui: &mut egui::Ui) {
        card(ui, |ui| {
            self.hotkey_row(ui, "Speak selection", "Reads the highlighted text.", Recording::Speak);
            divider(ui);
            self.hotkey_row(ui, "Change mode", "Cycles Verbatim, Summary, Explain and ELI5.", Recording::Mode);
        });
        if !self.hotkey_error.is_empty() {
            ui.colored_label(ui.visuals().warn_fg_color, &self.hotkey_error);
        }
        ui.label(
            RichText::new(if cfg!(target_os = "linux") {
                "Click a shortcut, then press the new keys. On GNOME these appear under Settings > Keyboard > Custom Shortcuts; elsewhere, bind `dynamite-tts speak` and `dynamite-tts toggle-mode` yourself."
            } else {
                "Click a shortcut, then press the new keys. Esc cancels."
            })
            .small()
            .weak(),
        );
        ui.add_space(8.0);
        card(ui, |ui| {
            row(
                ui,
                "Shortcut stops speech",
                "Pressing Speak while talking stops it, instead of reading the new highlight.",
                |ui| {
                    toggle(ui, &mut self.settings.hotkey_stops_speech);
                },
            );
        });
    }

    fn hotkey_row(&mut self, ui: &mut egui::Ui, label: &str, hint: &str, which: Recording) {
        let current = match which {
            Recording::Speak => self.settings.speak_hotkey.clone(),
            Recording::Mode => self.settings.mode_hotkey.clone(),
        };
        let recording = self.recording == Some(which);
        let mut clicked = false;
        row(ui, label, hint, |ui| {
            clicked = if recording {
                ui.add(accent_button("Press keys…")).clicked()
            } else {
                let hk = Hotkey::parse(&current);
                keycaps(ui, &hk.map(|h| h.display()).unwrap_or(current)).clicked()
            };
        });
        if clicked {
            if recording {
                self.finish_recording();
            } else {
                self.start_recording(which);
            }
        }
    }

    fn general_page(&mut self, ui: &mut egui::Ui) {
        section_label(ui, if cfg!(target_os = "linux") { "Status notifications" } else { "Status display" });
        card(ui, |ui| {
            segmented(ui, &mut self.settings.status_style, &StatusStyle::ALL.map(|s| (s, s.title())));
            ui.horizontal(|ui| {
                ui.add(
                    egui::Label::new(RichText::new(status_style_hint(self.settings.status_style)).small().weak())
                        .wrap(),
                );
            });
            let can_preview = self.settings.status_style != StatusStyle::Off;
            if ui
                .add_enabled(can_preview, egui::Button::new("Preview"))
                .on_hover_text("Show it for a few seconds, as if speaking")
                .clicked()
            {
                self.save_now();
                if control::send("preview-status").is_none() {
                    self.shared().message = "Start Dynamite TTS to preview the status display.".into();
                }
            }
        });
        ui.add_space(8.0);
        section_label(ui, "General");
        card(ui, |ui| {
            row(ui, "Start at login", "Open Dynamite TTS when you sign in.", |ui| {
                toggle(ui, &mut self.settings.launch_at_login);
            });
            divider(ui);
            row(ui, "Stop sound", "A short chime when speech is stopped.", |ui| {
                toggle(ui, &mut self.settings.play_sound_on_stop);
            });
        });
        ui.add_space(8.0);
        section_label(ui, "Diagnostics");
        card(ui, |ui| {
            row(ui, "Log file", "Timings and phases only, never what was read.", |ui| {
                if ui.button("Open folder").clicked() {
                    open_folder(&paths::log_dir());
                }
            });
            ui.label(RichText::new(paths::log_file().display().to_string()).monospace().small().weak());
        });
    }
}

fn eq_preset_hint(preset: EqPreset) -> &'static str {
    match preset {
        EqPreset::Flat => "The voice as the model makes it.",
        EqPreset::Warm => "More body and a softer top, for thin or shrill voices.",
        EqPreset::Clear => "Less boom, more presence and air, for dull or muffled voices.",
        EqPreset::Bright => "Extra sparkle on top.",
        EqPreset::Soft => "Takes the edge off harsh S and T sounds.",
        EqPreset::Custom => "Your own settings. Pick a preset to start from one.",
    }
}

fn status_style_hint(style: StatusStyle) -> &'static str {
    let linux = cfg!(target_os = "linux");
    match style {
        StatusStyle::Off => "Nothing on screen. The tray icon still changes colour while it works.",
        StatusStyle::Minimal if linux => "Only mode changes and errors, as brief notifications.",
        StatusStyle::Minimal => {
            "A small, see-through pill with just the moving bars. Mode changes and errors still show briefly."
        }
        StatusStyle::Standard if linux => "A notification showing what it is doing: thinking, speaking, mode changes.",
        StatusStyle::Standard => {
            "Moving bars and a short label such as \"Speaking\" or \"Summarizing…\". Click it to stop."
        }
        StatusStyle::Detailed if linux => "Like Standard, with the current mode on a second line.",
        StatusStyle::Detailed => "A larger pill with the current mode and a hint for stopping.",
    }
}

// -------------------------------------------------------------------- widgets

fn nav_item(ui: &mut egui::Ui, page: Page, selected: bool) -> egui::Response {
    let (rect, response) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 36.0), egui::Sense::click());
    let visuals = ui.visuals();
    if selected {
        ui.painter().rect_filled(rect, 8, ACCENT.gamma_multiply(0.18));
    } else if response.hovered() {
        ui.painter().rect_filled(rect, 8, visuals.widgets.hovered.weak_bg_fill);
    }
    let color = if selected { ACCENT } else { visuals.text_color() };
    let font = egui::FontId::proportional(15.0);
    if selected {
        let bar = egui::Rect::from_center_size(rect.left_center() + Vec2::new(4.0, 0.0), Vec2::new(3.0, 16.0));
        ui.painter().rect_filled(bar, 2, ACCENT);
    }
    ui.painter().text(rect.left_center() + Vec2::new(16.0, 0.0), egui::Align2::LEFT_CENTER, page.title(), font, color);
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

/// "Qwen3 1.7B · 1.7 GB memory", for the model picker.
fn model_choice(m: &llm::ChatModel) -> String {
    format!("{}  ·  {} memory", m.title, llm::size_label(m.memory_estimate()))
}

fn card<R>(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui) -> R) -> R {
    let visuals = ui.visuals().clone();
    let fill = if visuals.dark_mode { Color32::from_gray(38) } else { Color32::from_gray(250) };
    egui::Frame::new()
        .fill(fill)
        .stroke(Stroke::new(1.0, if visuals.dark_mode { Color32::from_gray(55) } else { Color32::from_gray(222) }))
        .corner_radius(CornerRadius::same(10))
        .inner_margin(Margin::symmetric(16, 12))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            add(ui)
        })
        .inner
}

fn section_label(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text.to_uppercase()).small().strong().weak());
}

fn divider(ui: &mut egui::Ui) {
    let stroke =
        Stroke::new(1.0, if ui.visuals().dark_mode { Color32::from_gray(52) } else { Color32::from_gray(230) });
    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), egui::Sense::hover());
    ui.painter().hline(rect.x_range(), rect.center().y, stroke);
}

/// A title and a one-line description on the left, the control right-aligned.
fn row(ui: &mut egui::Ui, title: &str, hint: &str, control: impl FnOnce(&mut egui::Ui)) {
    ui.horizontal(|ui| {
        let text_width = (ui.available_width() - CONTROL_WIDTH - 24.0).max(160.0);
        ui.vertical(|ui| {
            ui.set_width(text_width);
            ui.spacing_mut().item_spacing.y = 2.0;
            ui.label(RichText::new(title).strong());
            ui.add(egui::Label::new(RichText::new(hint).small().weak()).wrap());
        });
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), control);
    });
}

fn accent_button(text: &str) -> egui::Button<'_> {
    egui::Button::new(RichText::new(text).color(Color32::WHITE).strong())
        .fill(ACCENT)
        .corner_radius(CornerRadius::same(8))
}

/// An iOS-style switch.
fn toggle(ui: &mut egui::Ui, on: &mut bool) -> egui::Response {
    let size = Vec2::new(40.0, 22.0);
    let (rect, mut response) = ui.allocate_exact_size(size, egui::Sense::click());
    if response.clicked() {
        *on = !*on;
        response.mark_changed();
    }
    let t = ui.ctx().animate_bool_responsive(response.id, *on);
    let off = if ui.visuals().dark_mode { Color32::from_gray(80) } else { Color32::from_gray(200) };
    let track = lerp_color(off, ACCENT, t);
    let radius = rect.height() / 2.0;
    ui.painter().rect_filled(rect, radius, track);
    let x = egui::lerp((rect.left() + radius)..=(rect.right() - radius), t);
    ui.painter().circle_filled(egui::pos2(x, rect.center().y), radius - 3.0, Color32::WHITE);
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

fn lerp_color(a: Color32, b: Color32, t: f32) -> Color32 {
    let mix = |x: u8, y: u8| (x as f32 + (y as f32 - x as f32) * t).round() as u8;
    Color32::from_rgb(mix(a.r(), b.r()), mix(a.g(), b.g()), mix(a.b(), b.b()))
}

/// A row of joined buttons, one selected.
fn segmented<T: PartialEq + Copy>(ui: &mut egui::Ui, value: &mut T, options: &[(T, &str)]) {
    let visuals = ui.visuals().clone();
    egui::Frame::new()
        .fill(if visuals.dark_mode { Color32::from_gray(30) } else { Color32::from_gray(232) })
        .corner_radius(CornerRadius::same(8))
        .inner_margin(Margin::same(3))
        .show(ui, |ui| {
            ui.spacing_mut().item_spacing.x = 2.0;
            // Inside a right-aligned row egui lays out right to left; keep the reading order.
            let rtl = ui.layout().prefer_right_to_left();
            ui.horizontal(|ui| {
                let mut ordered: Vec<_> = options.iter().collect();
                if rtl {
                    ordered.reverse();
                }
                for (option, label) in ordered {
                    let selected = *value == *option;
                    let text = if selected {
                        RichText::new(*label).color(Color32::WHITE).strong()
                    } else {
                        RichText::new(*label)
                    };
                    let button = egui::Button::new(text)
                        .fill(if selected { ACCENT } else { Color32::TRANSPARENT })
                        .stroke(Stroke::NONE)
                        .corner_radius(CornerRadius::same(6));
                    if ui.add(button).clicked() {
                        *value = *option;
                    }
                }
            });
        });
}

/// A shortcut drawn as keycaps ("Ctrl" "Shift" "S"); the whole thing is one button.
fn keycaps(ui: &mut egui::Ui, shortcut: &str) -> egui::Response {
    let visuals = ui.visuals().clone();
    let inner = ui.horizontal(|ui| {
        ui.spacing_mut().item_spacing.x = 4.0;
        // Inside a right-aligned row egui lays out right to left; keep the reading order.
        let mut parts: Vec<&str> = Vec::new();
        for (i, key) in shortcut.split('+').enumerate() {
            if i > 0 {
                parts.push("+");
            }
            parts.push(key);
        }
        if ui.layout().prefer_right_to_left() {
            parts.reverse();
        }
        for key in parts {
            if key == "+" {
                ui.label(RichText::new("+").weak());
                continue;
            }
            egui::Frame::new()
                .fill(if visuals.dark_mode { Color32::from_gray(58) } else { Color32::WHITE })
                .stroke(Stroke::new(
                    1.0,
                    if visuals.dark_mode { Color32::from_gray(80) } else { Color32::from_gray(200) },
                ))
                .corner_radius(CornerRadius::same(6))
                .inner_margin(Margin::symmetric(9, 4))
                .show(ui, |ui| ui.label(RichText::new(key).monospace().strong()));
        }
    });
    ui.interact(inner.response.rect, ui.scope_id().with(shortcut), egui::Sense::click())
        .on_hover_text("Click to change")
        .on_hover_cursor(egui::CursorIcon::PointingHand)
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

impl SettingsApp {
    /// The whole window: sidebar on the left, the selected page in a scroll area on the right.
    fn window(&mut self, ui: &mut egui::Ui) {
        let sidebar_fill = if ui.visuals().dark_mode { Color32::from_gray(24) } else { Color32::from_gray(240) };
        egui::Panel::left("nav")
            .exact_size(SIDEBAR_WIDTH)
            .resizable(false)
            .frame(egui::Frame::new().fill(sidebar_fill).inner_margin(Margin::symmetric(10, 0)))
            .show(ui, |ui| self.sidebar(ui));
        egui::CentralPanel::default()
            .frame(egui::Frame::central_panel(ui.style()).inner_margin(Margin {
                left: 26,
                right: 26,
                top: 14,
                bottom: 0,
            }))
            .show(ui, |ui| {
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| self.content(ui));
            });
    }
}

impl eframe::App for SettingsApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.check_raise(&ctx);
        self.record_hotkey(&ctx);
        self.window(ui);
        self.send_eq_live();
        self.save_if_due();
        // Often enough to notice a raise request from the tray promptly.
        ctx.request_repaint_after(Duration::from_millis(if self.dirty_since.is_some() { 100 } else { 250 }));
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

    /// Lays every page out headlessly: no panics, and each page's controls are drawn.
    #[test]
    fn every_page_lays_out() {
        let ctx = egui::Context::default();
        apply_style(&ctx);
        let mut app = SettingsApp::new();
        let expected: [(Page, ChatBackend, &[&str]); 5] = [
            (
                Page::Voice,
                ChatBackend::Local,
                &["Voice", "Speed", "Language", "TONE", "Warm", "Low", "High", "Output device", "Model folder"],
            ),
            (
                Page::Modes,
                ChatBackend::Local,
                &["Speak mode", "Verbatim", "Explain", "ELI5", "Chat model", "Built in", "Model"],
            ),
            (Page::Modes, ChatBackend::Lemonade, &["Chat model", "Lemonade model", "Server address"]),
            (Page::Shortcuts, ChatBackend::Local, &["Speak selection", "Change mode", "Ctrl", "Shortcut stops speech"]),
            (Page::General, ChatBackend::Local, &["Start at login", "Stop sound", "Log file"]),
        ];
        for (page, backend, wanted) in expected {
            app.settings.chat_backend = backend;
            app.page = page;
            let mut texts = Vec::new();
            // Panels and grids size themselves on a first pass; the second frame is the real one.
            for _ in 0..2 {
                let mut frame = ctx.run_ui(egui::RawInput::default(), |ui| app.window(ui));
                frame.textures_delta.clear();
                texts = frame
                    .shapes
                    .iter()
                    .filter_map(|c| match &c.shape {
                        egui::Shape::Text(t) => Some(t.galley.text().to_string()),
                        _ => None,
                    })
                    .collect();
            }
            for w in wanted.iter().chain(&["Dynamite TTS", "Voice", "Modes", "Shortcuts", "General"]) {
                assert!(texts.iter().any(|t| t == w), "{w} missing on {page:?}: {texts:?}");
            }
        }
        app.dirty_since = None;
    }

    /// Renders every page, in light and dark mode, to target/settings-snapshots/*.png with a
    /// small software rasterizer, so the layout can be looked at without a display or GPU.
    /// Run with `cargo test render_settings_snapshots -- --ignored`.
    #[test]
    #[ignore]
    fn render_settings_snapshots() {
        let out = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("target/settings-snapshots");
        std::fs::create_dir_all(&out).unwrap();
        for (theme, preference) in [("dark", egui::ThemePreference::Dark), ("light", egui::ThemePreference::Light)] {
            let ctx = egui::Context::default();
            ctx.set_theme(preference);
            apply_style(&ctx);
            let mut app = SettingsApp::new();
            let mut textures = std::collections::HashMap::new();
            for page in Page::ALL {
                app.page = page;
                let size = Vec2::new(760.0, 660.0);
                let input = || egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(egui::Pos2::ZERO, size)),
                    ..Default::default()
                };
                let mut output = ctx.run_ui(input(), |ui| app.window(ui));
                for _ in 0..3 {
                    apply_textures(&mut textures, &mut output.textures_delta);
                    output = ctx.run_ui(input(), |ui| app.window(ui));
                }
                apply_textures(&mut textures, &mut output.textures_delta);
                let primitives = ctx.tessellate(output.shapes, 1.0);
                let bg = if theme == "dark" { [27, 27, 27, 255] } else { [248, 248, 248, 255] };
                let image = rasterize(&primitives, &textures, size.x as usize, size.y as usize, bg);
                image.save(out.join(format!("{theme}-{}.png", page.title().to_lowercase()))).unwrap();
            }
            app.dirty_since = None;
        }
    }

    type Textures = std::collections::HashMap<egui::TextureId, egui::ColorImage>;

    fn apply_textures(textures: &mut Textures, delta: &mut egui::TexturesDelta) {
        for (id, d) in delta.set.iter().flat_map(|(id, ds)| ds.iter().map(move |d| (id, d))) {
            let egui::ImageData::Color(patch) = &d.image;
            match d.pos {
                None => {
                    textures.insert(*id, egui::ColorImage::clone(patch));
                }
                Some([x0, y0]) => {
                    let tex = textures.get_mut(id).expect("patch of a known texture");
                    for y in 0..patch.size[1] {
                        for x in 0..patch.size[0] {
                            tex.pixels[(y0 + y) * tex.size[0] + x0 + x] = patch.pixels[y * patch.size[0] + x];
                        }
                    }
                }
            }
        }
        delta.clear();
    }

    /// Barycentric triangle fill with bilinear texture sampling and premultiplied-alpha blending.
    fn rasterize(
        prims: &[egui::ClippedPrimitive],
        textures: &Textures,
        w: usize,
        h: usize,
        bg: [u8; 4],
    ) -> image::RgbaImage {
        let mut px = vec![[bg[0] as f32, bg[1] as f32, bg[2] as f32, 255.0]; w * h];
        let sample = |tex: &egui::ColorImage, u: f32, v: f32| -> [f32; 4] {
            let (tw, th) = (tex.size[0], tex.size[1]);
            let x = (u * tw as f32 - 0.5).clamp(0.0, tw as f32 - 1.0);
            let y = (v * th as f32 - 0.5).clamp(0.0, th as f32 - 1.0);
            let (x0, y0) = (x.floor() as usize, y.floor() as usize);
            let (x1, y1) = ((x0 + 1).min(tw - 1), (y0 + 1).min(th - 1));
            let (fx, fy) = (x - x0 as f32, y - y0 as f32);
            let get = |x: usize, y: usize| tex.pixels[y * tw + x].to_array().map(|c| c as f32);
            let (a, b, c, d) = (get(x0, y0), get(x1, y0), get(x0, y1), get(x1, y1));
            std::array::from_fn(|i| {
                let top = a[i] + (b[i] - a[i]) * fx;
                let bottom = c[i] + (d[i] - c[i]) * fx;
                top + (bottom - top) * fy
            })
        };
        for prim in prims {
            let egui::epaint::Primitive::Mesh(mesh) = &prim.primitive else { continue };
            let Some(tex) = textures.get(&mesh.texture_id) else { continue };
            let clip =
                prim.clip_rect.intersect(egui::Rect::from_min_size(egui::Pos2::ZERO, Vec2::new(w as f32, h as f32)));
            for tri in mesh.indices.as_chunks::<3>().0 {
                let [a, b, c] = [tri[0], tri[1], tri[2]].map(|i| mesh.vertices[i as usize]);
                let area = (b.pos.x - a.pos.x) * (c.pos.y - a.pos.y) - (b.pos.y - a.pos.y) * (c.pos.x - a.pos.x);
                if area.abs() < 1e-6 {
                    continue;
                }
                let min_x = a.pos.x.min(b.pos.x).min(c.pos.x).max(clip.min.x).floor() as i64;
                let max_x = a.pos.x.max(b.pos.x).max(c.pos.x).min(clip.max.x).ceil() as i64;
                let min_y = a.pos.y.min(b.pos.y).min(c.pos.y).max(clip.min.y).floor() as i64;
                let max_y = a.pos.y.max(b.pos.y).max(c.pos.y).min(clip.max.y).ceil() as i64;
                for y in min_y.max(0)..max_y.min(h as i64) {
                    for x in min_x.max(0)..max_x.min(w as i64) {
                        let (pxf, pyf) = (x as f32 + 0.5, y as f32 + 0.5);
                        let w0 = ((b.pos.x - pxf) * (c.pos.y - pyf) - (b.pos.y - pyf) * (c.pos.x - pxf)) / area;
                        let w1 = ((c.pos.x - pxf) * (a.pos.y - pyf) - (c.pos.y - pyf) * (a.pos.x - pxf)) / area;
                        let w2 = 1.0 - w0 - w1;
                        if w0 < -1e-4 || w1 < -1e-4 || w2 < -1e-4 {
                            continue;
                        }
                        let lerp = |p: f32, q: f32, r: f32| p * w0 + q * w1 + r * w2;
                        let t = sample(tex, lerp(a.uv.x, b.uv.x, c.uv.x), lerp(a.uv.y, b.uv.y, c.uv.y));
                        let (ca, cb, cc) = (a.color.to_array(), b.color.to_array(), c.color.to_array());
                        let src: [f32; 4] =
                            std::array::from_fn(|i| lerp(ca[i] as f32, cb[i] as f32, cc[i] as f32) * t[i] / 255.0);
                        let dst = &mut px[y as usize * w + x as usize];
                        let inv = 1.0 - src[3] / 255.0;
                        for i in 0..4 {
                            dst[i] = src[i] + dst[i] * inv;
                        }
                    }
                }
            }
        }
        let raw: Vec<u8> = px.iter().flat_map(|p| p.map(|c| c.clamp(0.0, 255.0) as u8)).collect();
        image::RgbaImage::from_raw(w as u32, h as u32, raw).unwrap()
    }
}
