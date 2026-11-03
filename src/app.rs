//! The tray application: one event loop on the main thread that owns the tray, the status
//! pill, the global shortcuts and the speech pipeline. Worker threads (selection capture,
//! speech, the control socket) talk to it only through [`UserEvent`]s.

use std::process::Child;
use std::sync::Arc;
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};

use global_hotkey::{GlobalHotKeyEvent, GlobalHotKeyManager, HotKeyState, hotkey::HotKey};
use tao::event::{ElementState, Event, MouseButton, StartCause, WindowEvent};
use tao::event_loop::{ControlFlow, EventLoopBuilder, EventLoopProxy, EventLoopWindowTarget};
use tray_icon::TrayIconEvent;
use tray_icon::menu::MenuEvent;

use crate::selection::{self, Capture};
use crate::settings::{Settings, SpeakMode, Store};
use crate::speech::{self, Speech};
use crate::status::{Kind, Status};
use crate::tray::Tray;
use crate::{autostart, control, log, text};

const NOTICE_TIME: Duration = Duration::from_millis(2500);
/// The settings window suspends the shortcuts while it records one; never longer than this.
const SUSPEND_LIMIT: Duration = Duration::from_secs(30);

#[derive(Debug)]
pub enum UserEvent {
    Control(String, Sender<String>),
    Hotkey(GlobalHotKeyEvent),
    Menu(MenuEvent),
    Tray(TrayIconEvent),
    Speech(speech::Event),
    Captured(Capture),
}

/// What to do once the loop is running (from the command line that started the app).
pub enum Initial {
    Nothing,
    Command(String),
    SpeakText(String),
}

struct Hotkeys {
    manager: Option<GlobalHotKeyManager>,
    speak: Option<HotKey>,
    mode: Option<HotKey>,
    bound: Option<(String, String)>,
    suspended_until: Option<Instant>,
}

struct App {
    proxy: EventLoopProxy<UserEvent>,
    store: Store,
    settings: Settings,
    speech: Speech,
    tray: Option<Tray>,
    status: Option<Status>,
    hotkeys: Hotkeys,
    notice_until: Option<Instant>,
    capturing: bool,
    settings_window: Option<Child>,
}

pub fn run(initial: Initial, _lock: control::InstanceLock) -> ! {
    let event_loop = EventLoopBuilder::<UserEvent>::with_user_event().build();
    #[cfg(target_os = "macos")]
    let event_loop = {
        use tao::platform::macos::{ActivationPolicy, EventLoopExtMacOS};
        let mut event_loop = event_loop;
        event_loop.set_activation_policy(ActivationPolicy::Accessory);
        event_loop
    };
    let proxy = event_loop.create_proxy();

    {
        let p = proxy.clone();
        GlobalHotKeyEvent::set_event_handler(Some(move |e| {
            let _ = p.send_event(UserEvent::Hotkey(e));
        }));
        let p = proxy.clone();
        MenuEvent::set_event_handler(Some(move |e| {
            let _ = p.send_event(UserEvent::Menu(e));
        }));
        let p = proxy.clone();
        TrayIconEvent::set_event_handler(Some(move |e| {
            let _ = p.send_event(UserEvent::Tray(e));
        }));
    }
    let p = std::sync::Mutex::new(proxy.clone());
    let control_handler: control::Handler = Arc::new(move |command, reply| {
        let _ = p.lock().unwrap_or_else(|e| e.into_inner()).send_event(UserEvent::Control(command, reply));
    });
    if let Err(e) = control::serve(control_handler) {
        log!("control socket failed: {e}");
    }

    let store = Store::default_location();
    let settings = store.load();
    let emit_proxy = std::sync::Mutex::new(proxy.clone());
    let emit: speech::Emit = Arc::new(move |e| {
        let _ = emit_proxy.lock().unwrap_or_else(|e| e.into_inner()).send_event(UserEvent::Speech(e));
    });
    let mut app = App {
        proxy,
        store,
        settings,
        speech: Speech::new(emit),
        tray: None,
        status: None,
        hotkeys: Hotkeys { manager: None, speak: None, mode: None, bound: None, suspended_until: None },
        notice_until: None,
        capturing: false,
        settings_window: None,
    };
    let mut initial = Some(initial);

    event_loop.run(move |event, target, flow| {
        match event {
            Event::NewEvents(StartCause::Init) => {
                app.start(target);
                match initial.take() {
                    Some(Initial::Command(c)) => {
                        app.handle_command(&c);
                    }
                    Some(Initial::SpeakText(t)) => app.speak_text(&t),
                    _ => {}
                }
            }
            Event::UserEvent(e) => app.handle(e, flow),
            Event::WindowEvent { window_id, event, .. } => {
                if app.status.as_ref().is_some_and(|s| s.window_id() == Some(window_id)) {
                    match event {
                        WindowEvent::MouseInput { state: ElementState::Pressed, button: MouseButton::Left, .. } => {
                            app.stop(true)
                        }
                        WindowEvent::CloseRequested => {}
                        _ => {}
                    }
                }
            }
            Event::RedrawRequested(_) => {
                if let Some(s) = app.status.as_mut() {
                    s.redraw();
                }
            }
            _ => {}
        }
        if *flow != ControlFlow::Exit {
            app.tick();
            *flow = match app.next_deadline() {
                Some(at) => ControlFlow::WaitUntil(at),
                None => ControlFlow::Wait,
            };
        }
    })
}

impl App {
    fn start(&mut self, target: &EventLoopWindowTarget<UserEvent>) {
        match Tray::new(self.settings.speak_mode) {
            Ok(t) => self.tray = Some(t),
            Err(e) => log!("tray unavailable: {e}"),
        }
        self.status = Status::new(target);
        #[cfg(not(target_os = "linux"))]
        match GlobalHotKeyManager::new() {
            Ok(m) => self.hotkeys.manager = Some(m),
            Err(e) => log!("global shortcuts unavailable: {e}"),
        }
        #[cfg(target_os = "linux")]
        if !crate::gnome::available() && std::env::var_os("WAYLAND_DISPLAY").is_none() {
            // X11 without GNOME: grab the keys ourselves.
            match GlobalHotKeyManager::new() {
                Ok(m) => self.hotkeys.manager = Some(m),
                Err(e) => log!("global shortcuts unavailable: {e}"),
            }
        }
        self.apply_settings(None);
        if let Some(missing) = selection::missing_requirement() {
            selection::request_permission();
            self.notice(&missing);
        }
        log!(
            "ready speak={} mode={} tray={}",
            self.settings.speak_hotkey,
            self.settings.mode_hotkey,
            self.tray.is_some()
        );
    }

    fn handle(&mut self, event: UserEvent, flow: &mut ControlFlow) {
        match event {
            UserEvent::Control(command, reply) => {
                let answer = self.handle_command(&command);
                let _ = reply.send(answer);
                if command == "quit" {
                    self.quit(flow);
                }
            }
            UserEvent::Hotkey(e) => {
                if e.state != HotKeyState::Pressed || self.hotkeys.suspended_until.is_some() {
                    return;
                }
                if self.hotkeys.speak.is_some_and(|h| h.id() == e.id) {
                    self.speak_selection();
                } else if self.hotkeys.mode.is_some_and(|h| h.id() == e.id) {
                    self.cycle_mode();
                }
            }
            UserEvent::Menu(e) => {
                let Some(tray) = &self.tray else { return };
                if e.id == tray.stop_id {
                    self.stop(true);
                } else if e.id == tray.mode_id {
                    self.cycle_mode();
                } else if e.id == tray.settings_id {
                    self.open_settings();
                } else if e.id == tray.quit_id {
                    self.quit(flow);
                }
            }
            UserEvent::Tray(e) => {
                // Windows: double-click opens Settings; the menu is on right-click.
                if let TrayIconEvent::DoubleClick { .. } = e {
                    self.open_settings();
                }
            }
            UserEvent::Speech(e) => {
                if !self.speech.accept(&e) {
                    return;
                }
                match e {
                    speech::Event::Phase(_, phase) => self.show_phase(phase.as_deref()),
                    speech::Event::Notice(_, message) => self.notice(&message),
                }
            }
            UserEvent::Captured(capture) => {
                self.capturing = false;
                match capture.text {
                    None if self.speech.is_busy() => self.stop(true),
                    None => {
                        self.show_phase(None);
                        self.notice("No text selected");
                    }
                    Some(text) => {
                        let mode = self.settings.speak_mode;
                        let context = (mode == SpeakMode::Explain && text::is_short_selection(&text))
                            .then(|| text::normalize_context(&text, capture.context.as_deref()))
                            .flatten();
                        if let Some(n) = self.speech.speak(&text, context, &self.settings, mode) {
                            self.notice(&n);
                        }
                        self.show_phase(self.speech.phase().map(str::to_string).as_deref());
                    }
                }
            }
        }
    }

    pub fn handle_command(&mut self, command: &str) -> String {
        match command {
            "speak" => self.speak_selection(),
            "toggle-mode" => self.cycle_mode(),
            "stop" => self.stop(true),
            "settings" => self.open_settings(),
            "quit" => {}
            "reload" => {
                let previous = std::mem::replace(&mut self.settings, self.store.load());
                self.apply_settings(Some(&previous));
            }
            "suspend-hotkeys" => self.suspend_hotkeys(),
            "resume-hotkeys" => self.resume_hotkeys(),
            "ping" => {
                let open = self.settings_open();
                return format!(
                    "ok phase={} mode={} engine={:?} settings={}",
                    self.speech.phase().unwrap_or("Ready"),
                    self.settings.speak_mode.as_str(),
                    self.settings.engine,
                    if open { "open" } else { "closed" }
                );
            }
            other => match other.strip_prefix("speak-text ") {
                Some(text) => self.speak_text(text),
                None => return format!("error: unknown command '{other}'"),
            },
        }
        "ok".into()
    }

    fn speak_text(&mut self, text: &str) {
        if let Some(n) = self.speech.speak(text, None, &self.settings, SpeakMode::Verbatim) {
            self.notice(&n);
        }
        self.show_phase(self.speech.phase().map(str::to_string).as_deref());
    }

    fn speak_selection(&mut self) {
        if self.speech.is_busy() && self.settings.hotkey_stops_speech {
            log!("hotkey: stopping current speech");
            self.stop(true);
            return;
        }
        if self.capturing {
            return;
        }
        self.capturing = true;
        let include_context = self.settings.speak_mode == SpeakMode::Explain;
        let proxy = self.proxy.clone();
        let started = Instant::now();
        std::thread::spawn(move || {
            let capture = selection::capture(include_context);
            log!(
                "hotkey: captured {} chars in {} ms",
                capture.text.as_deref().map_or(0, |t| t.chars().count()),
                started.elapsed().as_millis()
            );
            let _ = proxy.send_event(UserEvent::Captured(capture));
        });
    }

    fn stop(&mut self, chime: bool) {
        self.speech.stop(chime, &self.settings);
        self.show_phase(None);
    }

    fn cycle_mode(&mut self) {
        let mode = self.settings.speak_mode.next();
        self.settings.speak_mode = mode;
        if let Err(e) = self.store.save(&self.settings) {
            log!("could not save settings: {e}");
        }
        if let Some(t) = &self.tray {
            t.set_mode(mode);
        }
        log!("speak mode -> {}", mode.as_str());
        self.notice(&format!("Mode: {}", mode.title()));
    }

    /// Applies the current settings; `previous` is what they were before a reload. The login item
    /// is only touched when that setting changed, so one made by the installer survives.
    fn apply_settings(&mut self, previous: Option<&Settings>) {
        if let Some(t) = &self.tray {
            t.set_mode(self.settings.speak_mode);
        }
        if previous.is_some_and(|p| p.launch_at_login != self.settings.launch_at_login) {
            if let Err(e) = autostart::apply(self.settings.launch_at_login) {
                log!("launch at login failed: {e}");
            }
        }
        self.bind_hotkeys();
        if !self.settings.show_status {
            if let Some(s) = self.status.as_mut() {
                s.hide();
            }
        }
    }

    fn bind_hotkeys(&mut self) {
        let wanted = (self.settings.speak_hotkey.clone(), self.settings.mode_hotkey.clone());
        if self.hotkeys.bound.as_ref() == Some(&wanted) {
            return;
        }
        let speak = self.settings.speak_hotkey();
        let mode = self.settings.mode_hotkey();
        #[cfg(target_os = "linux")]
        if self.hotkeys.manager.is_none() {
            if crate::gnome::available() {
                crate::gnome::bind(&autostart::launcher_command().display().to_string(), &speak, &mode);
            } else {
                let exe = autostart::launcher_command();
                log!(
                    "bind these commands in your desktop's keyboard shortcuts: '{} speak' and '{} toggle-mode'",
                    exe.display(),
                    exe.display()
                );
            }
            self.hotkeys.bound = Some(wanted);
            return;
        }
        let Some(manager) = &self.hotkeys.manager else { return };
        for old in [self.hotkeys.speak.take(), self.hotkeys.mode.take()].into_iter().flatten() {
            let _ = manager.unregister(old);
        }
        let mut failed = Vec::new();
        for (hotkey, slot) in [(&speak, &mut self.hotkeys.speak), (&mode, &mut self.hotkeys.mode)] {
            match hotkey.to_global() {
                Some(global) => match manager.register(global) {
                    Ok(()) => *slot = Some(global),
                    Err(e) => {
                        log!("hotkey {} could not be registered: {e}", hotkey.to_storage());
                        failed.push(hotkey.display());
                    }
                },
                None => failed.push(hotkey.display()),
            }
        }
        self.hotkeys.bound = Some(wanted);
        if !failed.is_empty() {
            let verb = if failed.len() > 1 { "are" } else { "is" };
            self.notice(&format!("{} {verb} used by another app; pick another in Settings", failed.join(" and ")));
        } else {
            log!("hotkeys bound speak={} mode={}", speak.to_storage(), mode.to_storage());
        }
    }

    /// Releases the shortcuts while Settings records a new one: a registered key never reaches
    /// the window (Windows delivers it to the registration instead).
    fn suspend_hotkeys(&mut self) {
        self.hotkeys.suspended_until = Some(Instant::now() + SUSPEND_LIMIT);
        if let Some(manager) = &self.hotkeys.manager {
            for old in [self.hotkeys.speak.take(), self.hotkeys.mode.take()].into_iter().flatten() {
                let _ = manager.unregister(old);
            }
            self.hotkeys.bound = None;
        }
    }

    fn resume_hotkeys(&mut self) {
        self.hotkeys.suspended_until = None;
        self.bind_hotkeys();
    }

    fn show_phase(&mut self, phase: Option<&str>) {
        log!("phase: {}", phase.unwrap_or("Ready"));
        if let Some(t) = self.tray.as_mut() {
            t.set_phase(phase);
        }
        let Some(status) = self.status.as_mut() else { return };
        if !self.settings.show_status {
            status.hide();
            return;
        }
        match phase {
            Some(p) => {
                self.notice_until = None;
                status.show(p, if p == "Speaking" { Kind::Speaking } else { Kind::Working });
            }
            // A notice that just appeared (say, an error) outlives the phase ending.
            None if self.notice_until.is_some() => {}
            None => status.hide(),
        }
    }

    fn notice(&mut self, text: &str) {
        log!("notice: {text}");
        if !self.settings.show_status {
            return;
        }
        if let Some(status) = self.status.as_mut() {
            status.show(text, Kind::Notice);
            self.notice_until = Some(Instant::now() + NOTICE_TIME);
        }
    }

    fn tick(&mut self) {
        let now = Instant::now();
        if self.notice_until.is_some_and(|t| t <= now) {
            self.notice_until = None;
            let phase = self.speech.phase().map(str::to_string);
            match (phase, self.status.as_mut()) {
                (Some(p), Some(s)) if self.settings.show_status => {
                    s.show(&p, if p == "Speaking" { Kind::Speaking } else { Kind::Working })
                }
                (_, Some(s)) => s.hide(),
                _ => {}
            }
        }
        if self.hotkeys.suspended_until.is_some_and(|t| t <= now) {
            self.resume_hotkeys();
        }
    }

    fn next_deadline(&self) -> Option<Instant> {
        [self.notice_until, self.hotkeys.suspended_until].into_iter().flatten().min()
    }

    fn settings_open(&mut self) -> bool {
        match self.settings_window.as_mut().map(|c| c.try_wait()) {
            Some(Ok(None)) => true,
            Some(_) => {
                self.settings_window = None;
                false
            }
            None => false,
        }
    }

    fn open_settings(&mut self) {
        if self.settings_open() {
            // The window process raises itself when it gets this.
            let _ = crate::settings_ui::raise();
            return;
        }
        let Ok(exe) = std::env::current_exe() else { return };
        match std::process::Command::new(exe).arg("--settings-window").spawn() {
            Ok(child) => self.settings_window = Some(child),
            Err(e) => {
                log!("could not open Settings: {e}");
                self.notice("Could not open Settings");
            }
        }
    }

    fn quit(&mut self, flow: &mut ControlFlow) {
        log!("quit");
        self.speech.stop(false, &self.settings);
        if let Some(s) = self.status.as_mut() {
            s.hide();
        }
        if let Some(mut child) = self.settings_window.take() {
            let _ = child.kill();
        }
        control::cleanup();
        *flow = ControlFlow::Exit;
    }
}
