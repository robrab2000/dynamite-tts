//! What the app is doing, shown while it works.
//!
//! Windows and macOS: a pill at the top centre of the screen that never takes focus. It fades
//! in and out over half a second and shows animated bars: a travelling ripple while working,
//! and the real playback level while speaking. Clicking it stops speech. How much it shows is
//! the `StatusStyle` setting (off, minimal, standard, detailed).
//!
//! Linux: a desktop notification updated in place, because a Wayland compositor will not let
//! an app float a window without focus (GNOME draws the banner top-centre, where the pill sits
//! elsewhere).

#[cfg(any(windows, target_os = "macos"))]
use std::time::Duration;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Working,
    Speaking,
    Notice,
}

/// How long the pill takes to fade in, and again to fade out.
#[cfg(any(windows, target_os = "macos"))]
pub const FADE: Duration = Duration::from_millis(500);
/// Frame interval while the pill is visible (the bars animate).
#[cfg(any(windows, target_os = "macos"))]
pub const FRAME: Duration = Duration::from_millis(33);

#[cfg(any(windows, target_os = "macos"))]
pub use pill::Status;

#[cfg(target_os = "linux")]
pub use notify::Status;

/// Drawing the pill, independent of any window, so it can be tested and previewed anywhere.
#[cfg_attr(target_os = "linux", allow(dead_code))]
pub mod render {
    use super::Kind;
    use crate::settings::StatusStyle;

    const BG: [u8; 3] = [0x17, 0x18, 0x1c];
    const BORDER: [u8; 3] = [0x3a, 0x3c, 0x45];
    const FG: [u8; 3] = [0xf4, 0xf4, 0xf6];
    const DIM: [u8; 3] = [0x9a, 0xa0, 0xa8];

    /// The tray icon's colours: orange while working, the logo's blue while speaking.
    pub fn accent(kind: Kind) -> [u8; 3] {
        match kind {
            Kind::Working => [0xff, 0x9f, 0x0a],
            Kind::Speaking => [0x3d, 0x8b, 0xff],
            Kind::Notice => [0x9a, 0xa0, 0xa8],
        }
    }

    /// What the pill says.
    #[derive(Clone, Debug, PartialEq)]
    pub struct Look {
        pub style: StatusStyle,
        pub kind: Kind,
        pub text: String,
        /// The second line of the detailed style.
        pub detail: Option<String>,
    }

    /// Sizes in logical pixels for one style.
    struct Metrics {
        height: f32,
        pad: f32,
        bars: usize,
        bar_width: f32,
        bar_gap: f32,
        bar_height: f32,
        gap: f32,
        font: f32,
        detail_font: f32,
    }

    fn metrics(style: StatusStyle) -> Metrics {
        match style {
            StatusStyle::Minimal => Metrics {
                height: 26.0,
                pad: 12.0,
                bars: 5,
                bar_width: 3.0,
                bar_gap: 3.0,
                bar_height: 12.0,
                gap: 8.0,
                font: 12.0,
                detail_font: 0.0,
            },
            StatusStyle::Off | StatusStyle::Standard => Metrics {
                height: 42.0,
                pad: 20.0,
                bars: 5,
                bar_width: 3.5,
                bar_gap: 3.5,
                bar_height: 18.0,
                gap: 12.0,
                font: 14.0,
                detail_font: 0.0,
            },
            StatusStyle::Detailed => Metrics {
                height: 58.0,
                pad: 24.0,
                bars: 7,
                bar_width: 4.0,
                bar_gap: 4.0,
                bar_height: 28.0,
                gap: 16.0,
                font: 15.0,
                detail_font: 12.0,
            },
        }
    }

    /// The pill's full opacity: the minimal style stays a little see-through.
    pub fn max_opacity(style: StatusStyle) -> f32 {
        if style == StatusStyle::Minimal { 0.82 } else { 0.97 }
    }

    fn shows_text(look: &Look) -> bool {
        look.style != StatusStyle::Minimal || look.kind == Kind::Notice
    }

    fn text_width(font: Option<&fontdue::Font>, text: &str, px: f32) -> f32 {
        match font {
            Some(f) => text.chars().map(|c| f.metrics(c, px).advance_width).sum(),
            None => text.chars().count() as f32 * px * 0.55,
        }
    }

    /// The pill's size in physical pixels.
    pub fn size(look: &Look, font: Option<&fontdue::Font>, scale: f32) -> (u32, u32) {
        let m = metrics(look.style);
        let bars = m.bars as f32 * m.bar_width + (m.bars - 1) as f32 * m.bar_gap;
        let mut width = m.pad * 2.0 + bars;
        if shows_text(look) {
            let mut text = text_width(font, &look.text, m.font * scale) / scale;
            if let (StatusStyle::Detailed, Some(detail)) = (look.style, &look.detail) {
                text = text.max(text_width(font, detail, m.detail_font * scale) / scale);
            }
            width += m.gap + text;
        }
        ((width.clamp(m.height, 560.0) * scale).ceil() as u32, (m.height * scale).ceil() as u32)
    }

    /// Bar heights (0-1 of the tallest) at `time` seconds, with playback `level` (0-1).
    pub fn bar_heights(kind: Kind, bars: usize, time: f32, level: f32) -> Vec<f32> {
        let mid = (bars - 1) as f32 / 2.0;
        (0..bars)
            .map(|i| {
                let i_f = i as f32;
                // Taller in the middle, like the logo's waveform.
                let shape = 1.0 - ((i_f - mid) / (mid + 1.0)).powi(2) * 0.6;
                let h = match kind {
                    Kind::Speaking => {
                        let amp = (level * 4.0).clamp(0.0, 1.0);
                        let wobble = 0.5 + 0.5 * (time * 11.0 + i_f * 1.7).sin();
                        0.18 + 0.82 * amp * shape * (0.55 + 0.45 * wobble)
                    }
                    Kind::Working => 0.2 + 0.55 * shape * (0.5 + 0.5 * (time * 5.5 - i_f * 0.8).sin()),
                    Kind::Notice => 0.3 + 0.25 * shape,
                };
                h.clamp(0.12, 1.0)
            })
            .collect()
    }

    fn blend(pixel: &mut [u8; 3], color: [u8; 3], alpha: f32) {
        for (p, c) in pixel.iter_mut().zip(color) {
            *p = (*p as f32 * (1.0 - alpha) + c as f32 * alpha).round() as u8;
        }
    }

    /// Signed distance to a capsule (rounded rectangle with radius = half its short side).
    fn capsule(px: f32, py: f32, cx: f32, cy: f32, w: f32, h: f32) -> f32 {
        let r = w.min(h) / 2.0;
        let qx = (px - cx).abs() - (w / 2.0 - r);
        let qy = (py - cy).abs() - (h / 2.0 - r);
        (qx.max(0.0).powi(2) + qy.max(0.0).powi(2)).sqrt() + qx.max(qy).min(0.0) - r
    }

    #[allow(clippy::too_many_arguments)]
    fn text(
        pixels: &mut [[u8; 3]],
        width: usize,
        height: usize,
        font: &fontdue::Font,
        px: f32,
        x: f32,
        baseline: f32,
        color: [u8; 3],
        s: &str,
    ) {
        let mut pen = x;
        for c in s.chars() {
            let (m, bitmap) = font.rasterize(c, px);
            let gx = pen.round() as i64 + m.xmin as i64;
            let gy = (baseline - m.height as f32 - m.ymin as f32).round() as i64;
            for row in 0..m.height {
                for col in 0..m.width {
                    let (x, y) = (gx + col as i64, gy + row as i64);
                    if x < 0 || y < 0 || x as usize >= width || y as usize >= height {
                        continue;
                    }
                    let a = bitmap[row * m.width + col] as f32 / 255.0;
                    if a > 0.0 {
                        blend(&mut pixels[y as usize * width + x as usize], color, a);
                    }
                }
            }
            pen += m.advance_width;
        }
    }

    /// One frame of the pill, `width` x `height` physical pixels, row by row.
    pub fn draw(
        look: &Look,
        font: Option<&fontdue::Font>,
        scale: f32,
        (width, height): (u32, u32),
        time: f32,
        level: f32,
    ) -> Vec<[u8; 3]> {
        let m = metrics(look.style);
        let (w, h) = (width as usize, height as usize);
        let mut pixels = vec![BG; w * h];
        let (fw, fh) = (width as f32, height as f32);

        // A hairline border just inside the pill's edge.
        let border = scale.max(1.0);
        for y in 0..h {
            for x in 0..w {
                let d = capsule(x as f32 + 0.5, y as f32 + 0.5, fw / 2.0, fh / 2.0, fw, fh);
                let a = (1.0 - (d + border).abs() / border).clamp(0.0, 1.0) * 0.9;
                if a > 0.0 {
                    blend(&mut pixels[y * w + x], BORDER, a);
                }
            }
        }

        // The bars, as capsules around the vertical centre.
        let color = super::render::accent(look.kind);
        let heights = bar_heights(look.kind, m.bars, time, level);
        let (bw, gap, tallest) = (m.bar_width * scale, m.bar_gap * scale, m.bar_height * scale);
        let mut x = m.pad * scale;
        for frac in heights {
            let bh = (tallest * frac).max(bw);
            let (cx, cy) = (x + bw / 2.0, fh / 2.0);
            let (x0, x1) = ((x - 1.0).max(0.0) as usize, ((x + bw + 1.0) as usize).min(w));
            let (y0, y1) = (((cy - bh / 2.0) - 1.0).max(0.0) as usize, ((cy + bh / 2.0 + 1.0) as usize).min(h));
            for py in y0..y1 {
                for px in x0..x1 {
                    let d = capsule(px as f32 + 0.5, py as f32 + 0.5, cx, cy, bw, bh);
                    let a = (0.5 - d).clamp(0.0, 1.0);
                    if a > 0.0 {
                        blend(&mut pixels[py * w + px], color, a);
                    }
                }
            }
            x += bw + gap;
        }

        let (Some(font), true) = (font, shows_text(look)) else { return pixels };
        let tx = x - gap + m.gap * scale;
        let px = m.font * scale;
        let lines = font.horizontal_line_metrics(px);
        let (ascent, descent) = lines.map_or((px * 0.8, -px * 0.2), |l| (l.ascent, l.descent));
        match (&look.detail, look.style) {
            (Some(detail), StatusStyle::Detailed) => {
                let dpx = m.detail_font * scale;
                let block = (ascent - descent) + dpx * 1.25;
                let top = (fh - block) / 2.0;
                text(&mut pixels, w, h, font, px, tx, top + ascent, FG, &look.text);
                text(&mut pixels, w, h, font, dpx, tx, top + (ascent - descent) + dpx * 1.05, DIM, detail);
            }
            _ => {
                let baseline = (fh + ascent + descent) / 2.0;
                text(&mut pixels, w, h, font, px, tx, baseline, FG, &look.text);
            }
        }
        pixels
    }

    pub fn load_font() -> Option<fontdue::Font> {
        let candidates: &[&str] = if cfg!(windows) {
            &[r"C:\Windows\Fonts\segoeuisb.ttf", r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arial.ttf"]
        } else if cfg!(target_os = "macos") {
            &[
                "/System/Library/Fonts/SFNS.ttf",
                "/System/Library/Fonts/Helvetica.ttc",
                "/System/Library/Fonts/Supplemental/Arial.ttf",
            ]
        } else {
            &["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf"]
        };
        candidates.iter().find_map(|path| {
            let bytes = std::fs::read(path).ok()?;
            fontdue::Font::from_bytes(bytes, fontdue::FontSettings::default()).ok()
        })
    }
}

#[cfg(any(windows, target_os = "macos"))]
mod pill {
    use std::num::NonZeroU32;
    use std::rc::Rc;
    use std::time::Instant;

    use tao::dpi::{PhysicalPosition, PhysicalSize};
    use tao::event_loop::EventLoopWindowTarget;
    use tao::window::{Window, WindowBuilder, WindowId};

    use super::render::{self, Look};
    use super::{FADE, FRAME, Kind};
    use crate::settings::StatusStyle;

    pub struct Status {
        window: Rc<Window>,
        surface: Option<softbuffer::Surface<Rc<Window>, Rc<Window>>>,
        font: Option<fontdue::Font>,
        style: StatusStyle,
        look: Option<Look>,
        size: (u32, u32),
        shown: bool,
        opacity: f32,
        target: f32,
        level: f32,
        born: Instant,
        last: Instant,
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
                .with_inner_size(PhysicalSize::new(200, 40));
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
            let now = Instant::now();
            Some(Status {
                window,
                surface,
                font: render::load_font(),
                style: StatusStyle::Standard,
                look: None,
                size: (0, 0),
                shown: false,
                opacity: 0.0,
                target: 0.0,
                level: 0.0,
                born: now,
                last: now,
            })
        }

        pub fn window_id(&self) -> Option<WindowId> {
            Some(self.window.id())
        }

        pub fn set_style(&mut self, style: StatusStyle) {
            self.style = style;
            if style == StatusStyle::Off {
                self.hide();
            } else if let Some(look) = self.look.clone().filter(|_| self.target > 0.0) {
                self.show(&look.text, look.kind, look.detail.as_deref());
            }
        }

        /// Shows (or updates) the pill; it fades in when it was hidden.
        pub fn show(&mut self, text: &str, kind: Kind, detail: Option<&str>) {
            if self.style == StatusStyle::Off {
                return;
            }
            let look = Look { style: self.style, kind, text: text.to_string(), detail: detail.map(str::to_string) };
            let scale = self.window.scale_factor() as f32;
            let size = render::size(&look, self.font.as_ref(), scale);
            if size != self.size {
                self.size = size;
                self.window.set_inner_size(PhysicalSize::new(size.0, size.1));
                #[cfg(windows)]
                windows::round(&self.window, size);
                #[cfg(target_os = "macos")]
                macos::round(&self.window, size.1 as f64 / scale as f64);
            }
            if let Some(monitor) = self.window.current_monitor().or_else(|| self.window.primary_monitor()) {
                let (mpos, msize) = (monitor.position(), monitor.size());
                // A little below the menu bar (and the notch) on macOS; just under the top edge on Windows.
                #[cfg(target_os = "macos")]
                let top = (macos::menu_bar_height().unwrap_or(37.0) + 3.0) * monitor.scale_factor();
                #[cfg(windows)]
                let top = 16.0 * monitor.scale_factor();
                let x = mpos.x + (msize.width as i32 - size.0 as i32) / 2;
                self.window.set_outer_position(PhysicalPosition::new(x, mpos.y + top as i32));
            }
            self.look = Some(look);
            self.target = render::max_opacity(self.style);
            if !self.shown {
                self.shown = true;
                self.opacity = 0.0;
                self.last = Instant::now();
                set_opacity(&self.window, 0.0);
                self.draw();
                platform_show(&self.window);
            } else {
                self.draw();
            }
        }

        /// Fades the pill out.
        pub fn hide(&mut self) {
            self.target = 0.0;
        }

        pub fn set_level(&mut self, level: f32) {
            // Rise fast, fall slowly, so the bars read as speech rather than flicker.
            let rate = if level > self.level { 0.6 } else { 0.18 };
            self.level += (level - self.level) * rate;
        }

        /// Advances the fade and the animation; returns when the next frame is due.
        pub fn tick(&mut self, now: Instant) -> Option<Instant> {
            if !self.shown {
                return None;
            }
            let dt = now.saturating_duration_since(self.last).as_secs_f32();
            self.last = now;
            let step = dt / FADE.as_secs_f32() * render::max_opacity(self.style);
            self.opacity = if self.target > self.opacity {
                (self.opacity + step).min(self.target)
            } else {
                (self.opacity - step).max(self.target)
            };
            set_opacity(&self.window, self.opacity);
            if self.opacity <= 0.0 && self.target <= 0.0 {
                self.shown = false;
                platform_hide(&self.window);
                return None;
            }
            self.draw();
            Some(now + FRAME)
        }

        pub fn redraw(&mut self) {
            if self.shown {
                self.draw();
            }
        }

        fn draw(&mut self) {
            let Some(look) = &self.look else { return };
            let (Some(w), Some(h)) = (NonZeroU32::new(self.size.0), NonZeroU32::new(self.size.1)) else { return };
            let time = self.born.elapsed().as_secs_f32();
            let scale = self.window.scale_factor() as f32;
            let pixels = render::draw(look, self.font.as_ref(), scale, self.size, time, self.level);
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

    #[cfg(windows)]
    use windows::{set_opacity, show as platform_show_inner};

    #[cfg(windows)]
    fn platform_show(window: &Window) {
        platform_show_inner(window, true);
    }

    #[cfg(windows)]
    fn platform_hide(window: &Window) {
        platform_show_inner(window, false);
    }

    #[cfg(target_os = "macos")]
    use macos::set_opacity;

    #[cfg(target_os = "macos")]
    fn platform_show(window: &Window) {
        macos::show(window, true);
    }

    #[cfg(target_os = "macos")]
    fn platform_hide(window: &Window) {
        macos::show(window, false);
    }

    #[cfg(windows)]
    mod windows {
        use tao::platform::windows::WindowExtWindows;
        use tao::window::Window;
        use windows_sys::Win32::Graphics::Gdi::{CreateRoundRectRgn, SetWindowRgn};
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GWL_EXSTYLE, GetWindowLongPtrW, HWND_TOPMOST, LWA_ALPHA, SW_HIDE, SW_SHOWNOACTIVATE, SWP_NOACTIVATE,
            SWP_NOMOVE, SWP_NOSIZE, SetLayeredWindowAttributes, SetWindowLongPtrW, SetWindowPos, ShowWindow,
            WS_EX_LAYERED, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        };

        /// Clips the window to a pill shape.
        pub fn round(window: &Window, (w, h): (u32, u32)) {
            let hwnd = window.hwnd() as _;
            let (w, h) = (w as i32, h as i32);
            // SAFETY: hwnd is our live window; SetWindowRgn takes ownership of the region.
            unsafe {
                let region = CreateRoundRectRgn(0, 0, w + 1, h + 1, h, h);
                SetWindowRgn(hwnd, region, 1);
            }
        }

        fn ensure_styles(hwnd: windows_sys::Win32::Foundation::HWND) {
            // Never activate (the user's app must keep focus and its selection); layered so the
            // whole window can fade.
            // SAFETY: hwnd is our live window.
            unsafe {
                let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
                let wanted = ex | (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED) as isize;
                if wanted != ex {
                    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, wanted);
                }
            }
        }

        pub fn set_opacity(window: &Window, opacity: f32) {
            let hwnd = window.hwnd() as _;
            ensure_styles(hwnd);
            // SAFETY: hwnd is our live, layered window.
            unsafe { SetLayeredWindowAttributes(hwnd, 0, (opacity.clamp(0.0, 1.0) * 255.0) as u8, LWA_ALPHA) };
        }

        pub fn show(window: &Window, visible: bool) {
            let hwnd = window.hwnd() as _;
            // SAFETY: hwnd is our live window.
            unsafe {
                if visible {
                    ensure_styles(hwnd);
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
        use objc2::encode::{Encode, Encoding};
        use objc2::runtime::AnyObject;
        use objc2::{class, msg_send};
        use tao::platform::macos::WindowExtMacOS;
        use tao::window::Window;

        /// NSStatusWindowLevel: above normal and floating windows.
        const STATUS_LEVEL: isize = 25;

        #[repr(C)]
        #[derive(Clone, Copy)]
        struct CGPoint {
            x: f64,
            y: f64,
        }

        #[repr(C)]
        #[derive(Clone, Copy)]
        struct CGSize {
            width: f64,
            height: f64,
        }

        /// NSRect, which is CGRect on 64-bit macOS.
        #[repr(C)]
        #[derive(Clone, Copy)]
        struct CGRect {
            origin: CGPoint,
            size: CGSize,
        }

        // SAFETY: the layouts match CoreGraphics' (two doubles; a point and a size).
        unsafe impl Encode for CGPoint {
            const ENCODING: Encoding = Encoding::Struct("CGPoint", &[f64::ENCODING, f64::ENCODING]);
        }
        unsafe impl Encode for CGSize {
            const ENCODING: Encoding = Encoding::Struct("CGSize", &[f64::ENCODING, f64::ENCODING]);
        }
        unsafe impl Encode for CGRect {
            const ENCODING: Encoding = Encoding::Struct("CGRect", &[CGPoint::ENCODING, CGSize::ENCODING]);
        }

        /// The menu bar's height on the main screen, in points (taller on Macs with a notch).
        pub fn menu_bar_height() -> Option<f64> {
            // SAFETY: NSScreen.mainScreen, frame and visibleFrame exist on every supported macOS;
            // the screen may be null (headless), which is checked.
            unsafe {
                let screen: *mut AnyObject = msg_send![class!(NSScreen), mainScreen];
                if screen.is_null() {
                    return None;
                }
                let frame: CGRect = msg_send![screen, frame];
                let visible: CGRect = msg_send![screen, visibleFrame];
                let height = (frame.origin.y + frame.size.height) - (visible.origin.y + visible.size.height);
                (height.is_finite() && height >= 0.0).then_some(height)
            }
        }

        fn ns_window(window: &Window) -> Option<*mut AnyObject> {
            let w = window.ns_window() as *mut AnyObject;
            (!w.is_null()).then_some(w)
        }

        pub fn configure(window: &Window) {
            let Some(w) = ns_window(window) else { return };
            // SAFETY: a live NSWindow; these selectors exist on every macOS version we support.
            unsafe {
                let _: () = msg_send![w, setLevel: STATUS_LEVEL];
                let _: () = msg_send![w, setHidesOnDeactivate: false];
                // Transparent outside the rounded layer.
                let _: () = msg_send![w, setOpaque: false];
                let clear: *mut AnyObject = msg_send![class!(NSColor), clearColor];
                let _: () = msg_send![w, setBackgroundColor: clear];
                let _: () = msg_send![w, setHasShadow: true];
            }
        }

        /// Rounds the content view's layer into a pill `height` points tall.
        pub fn round(window: &Window, height: f64) {
            let Some(w) = ns_window(window) else { return };
            // SAFETY: a live NSWindow and its content view; the layer may be null before the
            // view has one, which is checked.
            unsafe {
                let view: *mut AnyObject = msg_send![w, contentView];
                if view.is_null() {
                    return;
                }
                let _: () = msg_send![view, setWantsLayer: true];
                let layer: *mut AnyObject = msg_send![view, layer];
                if layer.is_null() {
                    return;
                }
                let _: () = msg_send![layer, setCornerRadius: height / 2.0];
                let _: () = msg_send![layer, setMasksToBounds: true];
            }
        }

        pub fn set_opacity(window: &Window, opacity: f32) {
            let Some(w) = ns_window(window) else { return };
            // SAFETY: a live NSWindow.
            unsafe {
                let _: () = msg_send![w, setAlphaValue: opacity.clamp(0.0, 1.0) as f64];
            }
        }

        pub fn show(window: &Window, visible: bool) {
            let Some(w) = ns_window(window) else { return };
            // SAFETY: a live NSWindow. orderFrontRegardless shows it without activating the app.
            unsafe {
                if visible {
                    let _: () = msg_send![w, orderFrontRegardless];
                } else {
                    let _: () = msg_send![w, orderOut: std::ptr::null::<AnyObject>()];
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
    use crate::settings::StatusStyle;

    /// GNOME hides a banner after a few seconds; updating in place after that changes the entry
    /// in the message list but pops no new banner, so a fresh notification is made instead.
    const BANNER: Duration = Duration::from_secs(3);
    /// GNOME Shell can wedge its message tray when a notification is closed moments after it
    /// appeared, so closes wait until it has been up this long.
    const MIN_LIFETIME: Duration = Duration::from_secs(2);

    pub struct Status {
        handle: Option<(NotificationHandle, Instant)>,
        icon: String,
        style: StatusStyle,
    }

    impl Status {
        pub fn new<T: 'static>(_target: &tao::event_loop::EventLoopWindowTarget<T>) -> Option<Status> {
            let icon =
                crate::tray::icon_file().map(|p| p.display().to_string()).unwrap_or_else(|| "dynamite-tts".into());
            Some(Status { handle: None, icon, style: StatusStyle::Standard })
        }

        pub fn set_style(&mut self, style: StatusStyle) {
            self.style = style;
            if style == StatusStyle::Off {
                self.hide();
            }
        }

        /// Off shows nothing, minimal only notices (mode changes, errors); standard and
        /// detailed also show what the app is doing, detailed with a second line.
        pub fn show(&mut self, text: &str, kind: Kind, detail: Option<&str>) {
            match (self.style, kind) {
                (StatusStyle::Off, _) | (StatusStyle::Minimal, Kind::Working | Kind::Speaking) => return,
                _ => {}
            }
            let body = match (self.style, detail) {
                (StatusStyle::Detailed, Some(d)) => format!("{text}\n{d}"),
                _ => text.to_string(),
            };
            if self.handle.as_ref().is_some_and(|(_, at)| at.elapsed() > BANNER) {
                self.hide();
            }
            match self.handle.as_mut() {
                Some((handle, at)) => {
                    handle.body(&body);
                    let _ = handle.update();
                    *at = Instant::now();
                }
                None => {
                    let shown = Notification::new()
                        .appname("Dynamite TTS")
                        .summary("Dynamite TTS")
                        .body(&body)
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

        pub fn set_level(&mut self, _level: f32) {}

        pub fn tick(&mut self, _now: Instant) -> Option<Instant> {
            None
        }

        pub fn window_id(&self) -> Option<tao::window::WindowId> {
            None
        }

        pub fn redraw(&mut self) {}
    }
}

#[cfg(test)]
mod tests {
    use super::render::{self, Look};
    use super::*;
    use crate::settings::StatusStyle;

    fn look(style: StatusStyle, kind: Kind) -> Look {
        Look { style, kind, text: "Speaking".into(), detail: Some("Verbatim mode · click to stop".into()) }
    }

    #[test]
    fn styles_grow_from_minimal_to_detailed() {
        let sizes: Vec<_> = [StatusStyle::Minimal, StatusStyle::Standard, StatusStyle::Detailed]
            .map(|s| render::size(&look(s, Kind::Speaking), None, 1.0))
            .to_vec();
        assert!(sizes[0].0 < sizes[1].0 && sizes[1].0 < sizes[2].0, "{sizes:?}");
        assert!(sizes[0].1 < sizes[1].1 && sizes[1].1 < sizes[2].1, "{sizes:?}");
        // Minimal shows text only for notices.
        let notice = render::size(&look(StatusStyle::Minimal, Kind::Notice), None, 1.0);
        assert!(notice.0 > sizes[0].0);
    }

    #[test]
    fn bars_follow_the_level_while_speaking() {
        let quiet = render::bar_heights(Kind::Speaking, 5, 1.0, 0.0);
        let loud = render::bar_heights(Kind::Speaking, 5, 1.0, 0.3);
        assert!(quiet.iter().all(|h| *h <= 0.2));
        assert!(loud.iter().sum::<f32>() > quiet.iter().sum::<f32>() * 2.0);
        // Working ripples on its own, without audio.
        let a = render::bar_heights(Kind::Working, 5, 0.0, 0.0);
        let b = render::bar_heights(Kind::Working, 5, 0.3, 0.0);
        assert_ne!(a, b);
    }

    #[test]
    fn draws_every_style_without_a_font() {
        for style in [StatusStyle::Minimal, StatusStyle::Standard, StatusStyle::Detailed] {
            for kind in [Kind::Working, Kind::Speaking, Kind::Notice] {
                let l = look(style, kind);
                let size = render::size(&l, None, 2.0);
                let px = render::draw(&l, None, 2.0, size, 0.5, 0.2);
                assert_eq!(px.len(), (size.0 * size.1) as usize);
            }
        }
    }

    /// Writes every style and state to target/status-snapshots/*.png, on light and dark
    /// backgrounds, with the system font. Run with `cargo test status_snapshots -- --ignored`.
    #[test]
    #[ignore]
    fn status_snapshots() {
        let out = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("target/status-snapshots");
        std::fs::create_dir_all(&out).unwrap();
        let font = render::load_font();
        for style in [StatusStyle::Minimal, StatusStyle::Standard, StatusStyle::Detailed] {
            for (kind, text, detail) in [
                (Kind::Working, "Summarizing…", Some("Summary mode · click to stop")),
                (Kind::Speaking, "Speaking", Some("Verbatim mode · click to stop")),
                (Kind::Notice, "Mode: Explain", None),
            ] {
                let l = Look { style, kind, text: text.into(), detail: detail.map(str::to_string) };
                let size = render::size(&l, font.as_ref(), 2.0);
                let px = render::draw(&l, font.as_ref(), 2.0, size, 0.8, 0.18);
                let mut img = image::RgbaImage::new(size.0 + 40, size.1 + 40);
                for p in img.pixels_mut() {
                    *p = image::Rgba([0x6b, 0x6a, 0x2a, 255]);
                }
                for (i, [r, g, b]) in px.into_iter().enumerate() {
                    let (x, y) = (i as u32 % size.0, i as u32 / size.0);
                    // The window is clipped to a capsule; show that here.
                    let (cx, cy, hw, hh) = (size.0 as f32 / 2.0, size.1 as f32 / 2.0, size.0 as f32, size.1 as f32);
                    let r_ = hh / 2.0;
                    let qx = (x as f32 + 0.5 - cx).abs() - (hw / 2.0 - r_);
                    let qy = (y as f32 + 0.5 - cy).abs() - (hh / 2.0 - r_);
                    let d = (qx.max(0.0).powi(2) + qy.max(0.0).powi(2)).sqrt() + qx.max(qy).min(0.0) - r_;
                    if d <= 0.0 {
                        img.put_pixel(x + 20, y + 20, image::Rgba([r, g, b, 255]));
                    }
                }
                let name = format!("{}-{:?}.png", style.title().to_lowercase(), kind).to_lowercase();
                img.save(out.join(name)).unwrap();
            }
        }
    }
}
