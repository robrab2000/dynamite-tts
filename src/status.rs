//! What the app is doing, shown while it works.
//!
//! Windows and macOS: a small always-on-top pill at the top centre of the screen that never
//! takes focus; clicking it stops speech. Linux: a desktop notification updated in place,
//! because a Wayland compositor will not let an app float a window without focus (GNOME draws
//! the banner top-centre, where the pill sits elsewhere).

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Working,
    Speaking,
    Notice,
}

#[cfg(any(windows, target_os = "macos"))]
pub use pill::Status;

#[cfg(target_os = "linux")]
pub use notify::Status;

#[cfg(any(windows, target_os = "macos"))]
mod pill {
    use std::num::NonZeroU32;
    use std::rc::Rc;

    use tao::dpi::{LogicalSize, PhysicalPosition, PhysicalSize};
    use tao::event_loop::EventLoopWindowTarget;
    use tao::window::{Window, WindowBuilder, WindowId};

    use super::Kind;

    const HEIGHT: f64 = 34.0;
    const FONT_PX: f32 = 14.0;
    const PAD: f64 = 16.0;
    const DOT: f64 = 9.0;
    const GAP: f64 = 9.0;
    const BG: [u8; 3] = [0x1f, 0x20, 0x24];
    const FG: [u8; 3] = [0xf2, 0xf2, 0xf2];

    fn dot_color(kind: Kind) -> [u8; 3] {
        match kind {
            Kind::Working => [0xff, 0x9f, 0x0a],
            Kind::Speaking => [0x30, 0xd1, 0x58],
            Kind::Notice => [0x64, 0xa8, 0xff],
        }
    }

    fn load_font() -> Option<fontdue::Font> {
        let candidates: &[&str] = if cfg!(windows) {
            &[r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arial.ttf"]
        } else {
            &[
                "/System/Library/Fonts/SFNS.ttf",
                "/System/Library/Fonts/Helvetica.ttc",
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/Library/Fonts/Arial Unicode.ttf",
            ]
        };
        candidates.iter().find_map(|path| {
            let bytes = std::fs::read(path).ok()?;
            fontdue::Font::from_bytes(bytes, fontdue::FontSettings::default()).ok()
        })
    }

    pub struct Status {
        window: Rc<Window>,
        surface: Option<softbuffer::Surface<Rc<Window>, Rc<Window>>>,
        font: Option<fontdue::Font>,
        text: String,
        kind: Kind,
        visible: bool,
    }

    impl Status {
        pub fn new<T: 'static>(target: &EventLoopWindowTarget<T>) -> Option<Status> {
            let builder = WindowBuilder::new()
                .with_title("Dynamite TTS")
                .with_decorations(false)
                .with_resizable(false)
                .with_always_on_top(true)
                .with_visible(false)
                .with_focused(false)
                .with_focusable(false)
                .with_visible_on_all_workspaces(true)
                .with_inner_size(LogicalSize::new(200.0, HEIGHT));
            #[cfg(windows)]
            let builder = {
                use tao::platform::windows::WindowBuilderExtWindows;
                builder.with_skip_taskbar(true).with_undecorated_shadow(false)
            };
            let window = Rc::new(builder.build(target).ok()?);
            let surface = softbuffer::Context::new(window.clone())
                .and_then(|context| softbuffer::Surface::new(&context, window.clone()))
                .ok();
            #[cfg(target_os = "macos")]
            macos::configure(&window);
            Some(Status {
                window,
                surface,
                font: load_font(),
                text: String::new(),
                kind: Kind::Working,
                visible: false,
            })
        }

        pub fn window_id(&self) -> Option<WindowId> {
            Some(self.window.id())
        }

        pub fn show(&mut self, text: &str, kind: Kind) {
            self.text = text.to_string();
            self.kind = kind;
            let scale = self.window.scale_factor();
            let text_w = self.text_width(FONT_PX * scale as f32) as f64 / scale;
            let width = (PAD * 2.0 + DOT + GAP + text_w).clamp(120.0, 520.0);
            let size: PhysicalSize<u32> = LogicalSize::new(width, HEIGHT).to_physical(scale);
            self.window.set_inner_size(size);
            if let Some(monitor) = self.window.current_monitor().or_else(|| self.window.primary_monitor()) {
                let (mpos, msize) = (monitor.position(), monitor.size());
                // Below the menu bar on macOS; just under the top edge on Windows.
                let top = if cfg!(target_os = "macos") { 34.0 } else { 14.0 } * monitor.scale_factor();
                let x = mpos.x + (msize.width as i32 - size.width as i32) / 2;
                self.window.set_outer_position(PhysicalPosition::new(x, mpos.y + top as i32));
            }
            self.draw(size);
            if !self.visible {
                self.visible = true;
                platform_show(&self.window, size);
            } else {
                #[cfg(windows)]
                windows::round(&self.window, size);
            }
        }

        pub fn hide(&mut self) {
            if self.visible {
                self.visible = false;
                platform_hide(&self.window);
            }
        }

        pub fn redraw(&mut self) {
            if self.visible {
                let size = self.window.inner_size();
                self.draw(size);
            }
        }

        fn text_width(&self, px: f32) -> f32 {
            let Some(font) = &self.font else { return 8.0 * self.text.chars().count() as f32 };
            self.text.chars().map(|c| font.metrics(c, px).advance_width).sum()
        }

        fn draw(&mut self, size: PhysicalSize<u32>) {
            let (Some(w), Some(h)) = (NonZeroU32::new(size.width), NonZeroU32::new(size.height)) else { return };
            let scale = self.window.scale_factor();
            let px = FONT_PX * scale as f32;
            let (width, height) = (size.width as usize, size.height as usize);
            let mut pixels = vec![BG; width * height];

            // The dot, anti-aliased by distance from its edge.
            let radius = DOT * scale / 2.0;
            let (cx, cy) = (PAD * scale + radius, height as f64 / 2.0);
            let color = dot_color(self.kind);
            for y in 0..height {
                for x in 0..width {
                    let d = ((x as f64 + 0.5 - cx).powi(2) + (y as f64 + 0.5 - cy).powi(2)).sqrt();
                    let coverage = (radius + 0.5 - d).clamp(0.0, 1.0) as f32;
                    if coverage > 0.0 {
                        blend(&mut pixels[y * width + x], color, coverage);
                    }
                }
            }

            if let Some(font) = &self.font {
                let lines = font.horizontal_line_metrics(px);
                let ascent = lines.map_or(px * 0.8, |l| l.ascent);
                let descent = lines.map_or(-px * 0.2, |l| l.descent);
                let baseline = (height as f32 + ascent + descent) / 2.0;
                let mut pen = ((PAD + DOT + GAP) * scale) as f32;
                for c in self.text.chars() {
                    let (metrics, bitmap) = font.rasterize(c, px);
                    let gx = pen.round() as i64 + metrics.xmin as i64;
                    let gy = (baseline - metrics.height as f32 - metrics.ymin as f32).round() as i64;
                    for row in 0..metrics.height {
                        for col in 0..metrics.width {
                            let (x, y) = (gx + col as i64, gy + row as i64);
                            if x < 0 || y < 0 || x as usize >= width || y as usize >= height {
                                continue;
                            }
                            let coverage = bitmap[row * metrics.width + col] as f32 / 255.0;
                            if coverage > 0.0 {
                                blend(&mut pixels[y as usize * width + x as usize], FG, coverage);
                            }
                        }
                    }
                    pen += metrics.advance_width;
                }
            }

            let Some(surface) = self.surface.as_mut() else { return };
            if surface.resize(w, h).is_err() {
                return;
            }
            let Ok(mut buffer) = surface.buffer_mut() else { return };
            for (out, [r, g, b]) in buffer.iter_mut().zip(pixels) {
                *out = (r as u32) << 16 | (g as u32) << 8 | b as u32;
            }
            let _ = buffer.present();
        }
    }

    fn blend(pixel: &mut [u8; 3], color: [u8; 3], alpha: f32) {
        for i in 0..3 {
            pixel[i] = (pixel[i] as f32 * (1.0 - alpha) + color[i] as f32 * alpha).round() as u8;
        }
    }

    #[cfg(windows)]
    fn platform_show(window: &Window, size: PhysicalSize<u32>) {
        windows::round(window, size);
        windows::show(window, true);
    }

    #[cfg(windows)]
    fn platform_hide(window: &Window) {
        windows::show(window, false);
    }

    #[cfg(target_os = "macos")]
    fn platform_show(window: &Window, _size: PhysicalSize<u32>) {
        macos::show(window, true);
    }

    #[cfg(target_os = "macos")]
    fn platform_hide(window: &Window) {
        macos::show(window, false);
    }

    #[cfg(windows)]
    mod windows {
        use tao::dpi::PhysicalSize;
        use tao::platform::windows::WindowExtWindows;
        use tao::window::Window;
        use windows_sys::Win32::Graphics::Gdi::{CreateRoundRectRgn, SetWindowRgn};
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GWL_EXSTYLE, GetWindowLongPtrW, HWND_TOPMOST, SW_HIDE, SW_SHOWNOACTIVATE, SWP_NOACTIVATE, SWP_NOMOVE,
            SWP_NOSIZE, SetWindowLongPtrW, SetWindowPos, ShowWindow, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        };

        /// Clips the window to a pill shape.
        pub fn round(window: &Window, size: PhysicalSize<u32>) {
            let hwnd = window.hwnd() as _;
            let (w, h) = (size.width as i32, size.height as i32);
            // SAFETY: hwnd is our live window; SetWindowRgn takes ownership of the region.
            unsafe {
                let region = CreateRoundRectRgn(0, 0, w + 1, h + 1, h, h);
                SetWindowRgn(hwnd, region, 1);
            }
        }

        pub fn show(window: &Window, visible: bool) {
            let hwnd = window.hwnd() as _;
            // SAFETY: hwnd is our live window.
            unsafe {
                if visible {
                    // Never activate: the user's app must keep focus (and its selection).
                    let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
                    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) as isize);
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                } else {
                    ShowWindow(hwnd, SW_HIDE);
                }
            }
        }
    }

    #[cfg(target_os = "macos")]
    mod macos {
        use objc2::msg_send;
        use objc2::runtime::AnyObject;
        use tao::platform::macos::WindowExtMacOS;
        use tao::window::Window;

        /// NSStatusWindowLevel: above normal and floating windows.
        const STATUS_LEVEL: isize = 25;

        pub fn configure(window: &Window) {
            let ns_window = window.ns_window() as *mut AnyObject;
            if ns_window.is_null() {
                return;
            }
            // SAFETY: a live NSWindow; both selectors exist on every macOS version we support.
            unsafe {
                let _: () = msg_send![ns_window, setLevel: STATUS_LEVEL];
                let _: () = msg_send![ns_window, setHidesOnDeactivate: false];
            }
        }

        pub fn show(window: &Window, visible: bool) {
            let ns_window = window.ns_window() as *mut AnyObject;
            if ns_window.is_null() {
                return;
            }
            // SAFETY: a live NSWindow. orderFrontRegardless shows it without activating the app.
            unsafe {
                if visible {
                    let _: () = msg_send![ns_window, orderFrontRegardless];
                } else {
                    let _: () = msg_send![ns_window, orderOut: std::ptr::null::<AnyObject>()];
                }
            }
        }
    }
}

#[cfg(target_os = "linux")]
mod notify {
    use std::time::{Duration, Instant};

    use notify_rust::{Hint, Notification, NotificationHandle};

    use super::Kind;

    /// GNOME hides a banner after a few seconds; updating in place after that changes the entry
    /// in the message list but pops no new banner, so a fresh notification is made instead.
    const BANNER: Duration = Duration::from_secs(3);
    /// GNOME Shell can wedge its message tray when a notification is closed moments after it
    /// appeared, so closes wait until it has been up this long.
    const MIN_LIFETIME: Duration = Duration::from_secs(2);

    pub struct Status {
        handle: Option<(NotificationHandle, Instant)>,
        icon: String,
    }

    impl Status {
        pub fn new<T: 'static>(_target: &tao::event_loop::EventLoopWindowTarget<T>) -> Option<Status> {
            let icon =
                crate::tray::icon_file().map(|p| p.display().to_string()).unwrap_or_else(|| "dynamite-tts".into());
            Some(Status { handle: None, icon })
        }

        pub fn show(&mut self, text: &str, kind: Kind) {
            if self.handle.as_ref().is_some_and(|(_, at)| at.elapsed() > BANNER) {
                self.hide();
            }
            match self.handle.as_mut() {
                Some((handle, at)) => {
                    handle.body(text);
                    let _ = handle.update();
                    *at = Instant::now();
                }
                None => {
                    let shown = Notification::new()
                        .appname("Dynamite TTS")
                        .summary("Dynamite TTS")
                        .body(text)
                        .icon(&self.icon)
                        .hint(Hint::DesktopEntry("dynamite-tts".into()))
                        .hint(Hint::Transient(true))
                        .hint(Hint::Resident(kind != Kind::Notice))
                        .show();
                    match shown {
                        Ok(h) => self.handle = Some((h, Instant::now())),
                        Err(e) => crate::log!("notification failed: {e}"),
                    }
                }
            }
        }

        pub fn hide(&mut self) {
            let Some((handle, at)) = self.handle.take() else { return };
            let wait = MIN_LIFETIME.saturating_sub(at.elapsed());
            std::thread::spawn(move || {
                std::thread::sleep(wait + Duration::from_millis(50));
                handle.close();
            });
        }

        pub fn window_id(&self) -> Option<tao::window::WindowId> {
            None
        }

        pub fn redraw(&mut self) {}
    }
}
