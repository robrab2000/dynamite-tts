//! Regenerates the app and tray icons from the logo:
//!
//!     cargo run --example make_icons [assets/logo.png]
//!
//! The logo's wordmark is unreadable at icon sizes, so the icons use only its graphic (the
//! dynamite and the speech bubble), cut out of the black background:
//!
//! - assets/icons/{idle,active,speaking}.png: the tray icon on transparency, 128 px. Working
//!   and speaking add an orange or green badge in the top-left corner.
//! - assets/icons/app.png (512 px), app.ico and AppIcon.icns: the graphic on a dark rounded
//!   square, for the executable, the installer, the macOS Dock and the Linux launcher.

use std::path::Path;

use image::{Rgba, RgbaImage, imageops};

/// The graphic in the 2000 px logo (left, top, width, height), clear of the wordmark below it.
const GRAPHIC: (u32, u32, u32, u32) = (400, 110, 1120, 1035);
/// Background pixels are at most this bright; the cut-out fades them to transparent.
const BACKGROUND: f32 = 48.0;

/// How the tray recolours the bubble.s waveform and sound waves for each state.
#[derive(Clone, Copy)]
enum Waves {
    /// Ready: a quiet slate grey.
    Grey,
    /// Working: orange, like the spark.
    Orange,
    /// Speaking: green.
    Green,
}

fn main() {
    let source = std::env::args().nth(1).unwrap_or_else(|| "assets/logo.png".into());
    let logo = image::open(&source).unwrap_or_else(|e| panic!("{source}: {e}")).into_rgba8();
    assert_eq!(logo.dimensions(), (2000, 2000), "GRAPHIC is measured on the 2000 px logo");
    let (x, y, w, h) = GRAPHIC;
    let graphic = trim(&cut_out(&imageops::crop_imm(&logo, x, y, w, h).to_image()));
    let out = Path::new("assets/icons");

    // The spark is removed at full resolution, before shrinking, so its edges are clean.
    let lit = pad(&graphic, 0.04);
    let out_cold = unlit(&lit);
    let preview = Path::new("target/icon-preview");
    std::fs::create_dir_all(preview).unwrap();
    for (name, base, waves) in
        [("idle", &out_cold, Waves::Grey), ("active", &lit, Waves::Orange), ("speaking", &lit, Waves::Green)]
    {
        recolor_waves(&resize(base, 128), waves).save(out.join(format!("{name}.png"))).unwrap();
        recolor_waves(&resize(base, 512), waves).save(preview.join(format!("{name}.png"))).unwrap();
    }

    let app = app_icon(&graphic, 1024);
    resize(&app, 512).save(out.join("app.png")).unwrap();
    write_ico(&app, &out.join("app.ico"));
    write_icns(&app, &out.join("AppIcon.icns"));
    println!("wrote tray, app.png, app.ico and AppIcon.icns to {}", out.display());
}

/// Makes the background transparent: every dark pixel reachable from the edge without crossing
/// the artwork. Its alpha follows its brightness, so the spark's glow and anti-aliased edges
/// fade out instead of keeping a black fringe. Dark parts inside the artwork (the fuse, the
/// band) are not reachable and stay opaque.
fn cut_out(img: &RgbaImage) -> RgbaImage {
    let (w, h) = img.dimensions();
    let bright = |p: &Rgba<u8>| p.0[..3].iter().copied().max().unwrap_or(0) as f32;
    let mut background = vec![false; (w * h) as usize];
    let mut stack: Vec<(u32, u32)> = Vec::new();
    for i in 0..w {
        stack.extend([(i, 0), (i, h - 1)]);
    }
    for j in 0..h {
        stack.extend([(0, j), (w - 1, j)]);
    }
    while let Some((px, py)) = stack.pop() {
        let idx = (py * w + px) as usize;
        if background[idx] || bright(img.get_pixel(px, py)) > BACKGROUND {
            continue;
        }
        background[idx] = true;
        if px > 0 {
            stack.push((px - 1, py));
        }
        if py > 0 {
            stack.push((px, py - 1));
        }
        if px + 1 < w {
            stack.push((px + 1, py));
        }
        if py + 1 < h {
            stack.push((px, py + 1));
        }
    }
    let mut out = img.clone();
    for (px, py, p) in out.enumerate_pixels_mut() {
        if background[(py * w + px) as usize] {
            // Treat the pixel as colour over black: alpha from brightness, colour un-premultiplied.
            let a = (bright(p) / BACKGROUND).clamp(0.0, 1.0);
            let a = a * a; // a softer fall-off, so near-black noise vanishes
            if a < 0.02 {
                *p = Rgba([0, 0, 0, 0]);
            } else {
                let scale = 1.0 / (a.sqrt() * (BACKGROUND / 255.0)).max(1e-3);
                let c = |v: u8| ((v as f32 * scale).min(255.0)) as u8;
                *p = Rgba([c(p.0[0]), c(p.0[1]), c(p.0[2]), (a * 255.0) as u8]);
            }
        }
    }
    out
}

/// Crops to the visible pixels and centres them in a square.
fn trim(img: &RgbaImage) -> RgbaImage {
    let (mut x0, mut y0, mut x1, mut y1) = (u32::MAX, u32::MAX, 0, 0);
    for (x, y, p) in img.enumerate_pixels() {
        if p.0[3] > 24 {
            x0 = x0.min(x);
            y0 = y0.min(y);
            x1 = x1.max(x);
            y1 = y1.max(y);
        }
    }
    let content = imageops::crop_imm(img, x0, y0, x1 - x0 + 1, y1 - y0 + 1).to_image();
    let side = content.width().max(content.height());
    let mut square = RgbaImage::new(side, side);
    imageops::overlay(
        &mut square,
        &content,
        ((side - content.width()) / 2) as i64,
        ((side - content.height()) / 2) as i64,
    );
    square
}

/// Adds a transparent margin of `fraction` of the side on every edge.
fn pad(img: &RgbaImage, fraction: f32) -> RgbaImage {
    let margin = (img.width() as f32 * fraction) as u32;
    let side = img.width() + 2 * margin;
    let mut out = RgbaImage::new(side, side);
    imageops::overlay(&mut out, img, margin as i64, margin as i64);
    out
}

/// Lanczos resize in premultiplied alpha, so transparent pixels don't bleed dark halos.
fn resize(img: &RgbaImage, side: u32) -> RgbaImage {
    let mut pre = img.clone();
    for p in pre.pixels_mut() {
        let a = p.0[3] as u32;
        for c in 0..3 {
            p.0[c] = (p.0[c] as u32 * a / 255) as u8;
        }
    }
    let mut out = imageops::resize(&pre, side, side, imageops::FilterType::Lanczos3);
    for p in out.pixels_mut() {
        let a = p.0[3] as u32;
        for c in 0..3 {
            if let Some(v) = (p.0[c] as u32 * 255 + a / 2).checked_div(a) {
                p.0[c] = v.min(255) as u8;
            }
        }
    }
    out
}

/// Puts the spark out for the ready icon. It finds the spark by its bright yellow core (top
/// right). Around it, only the fuse survives: its dark body plus anything within an outline's
/// width of it (its highlight and light outline). The star, its outline, glow and embers go.
fn unlit(img: &RgbaImage) -> RgbaImage {
    let mut out = img.clone();
    let (w, h) = out.dimensions();
    let in_corner = |x: u32, y: u32| x as f32 > w as f32 * 0.6 && (y as f32) < h as f32 * 0.42;
    let (mut sx, mut sy, mut n) = (0.0, 0.0, 0.0);
    for (x, y, p) in img.enumerate_pixels() {
        let (hue, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
        if in_corner(x, y) && p.0[3] > 128 && (35.0..=65.0).contains(&hue) && s > 0.5 && v > 0.85 {
            (sx, sy, n) = (sx + x as f32, sy + y as f32, n + 1.0);
        }
    }
    assert!(n > 0.0, "no spark found");
    let (cx, cy) = (sx / n, sy / n);
    let (reach, core) = (w as f32 * 0.19, w as f32 * 0.045);
    let near_spark = |x: u32, y: u32| ((x as f32 - cx).powi(2) + (y as f32 - cy).powi(2)).sqrt();
    // The fuse's dark, unsaturated body, away from the white-hot tip.
    let body: Vec<(i32, i32)> = img
        .enumerate_pixels()
        .filter(|&(x, y, p)| {
            let d = near_spark(x, y);
            let (_, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
            d <= reach && d >= core && p.0[3] > 200 && v < 0.45 && s < 0.45
        })
        .map(|(x, y, _)| (x as i32, y as i32))
        .collect();
    // Anything within the outline's width of the body is part of the fuse.
    let outline = (w as f32 * 0.018).ceil() as i32;
    let mut keep = vec![false; (w * img.height()) as usize];
    for &(bx, by) in &body {
        for dy in -outline..=outline {
            for dx in -outline..=outline {
                let (x, y) = (bx + dx, by + dy);
                if dx * dx + dy * dy <= outline * outline
                    && x >= 0
                    && y >= 0
                    && (x as u32) < w
                    && (y as u32) < img.height()
                {
                    keep[(y as u32 * w + x as u32) as usize] = true;
                }
            }
        }
    }
    for (x, y, p) in out.enumerate_pixels_mut() {
        if near_spark(x, y) > reach {
            continue;
        }
        // The glowing tip is warm; the fuse never is.
        let (hue, s, _) = to_hsv(p.0[0], p.0[1], p.0[2]);
        let warm = (hue <= 75.0 || hue >= 300.0) && s > 0.2;
        if warm || !keep[(y * w + x) as usize] {
            *p = Rgba([0, 0, 0, 0]);
        }
    }
    out
}

/// Recolours the blue of the bubble's waveform and sound waves, keeping each pixel's
/// brightness and the logo's gradient.
fn recolor_waves(img: &RgbaImage, waves: Waves) -> RgbaImage {
    let mut out = img.clone();
    let (w, h) = out.dimensions();
    for (x, y, p) in out.enumerate_pixels_mut() {
        // Only the bubble and its waves (right of and below the dynamite): the band around the
        // dynamite is a dark blue too.
        if p.0[3] == 0 || (x as f32) < w as f32 * 0.345 || (y as f32) < h as f32 * 0.44 {
            continue;
        }
        let (h, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
        if !(185.0..=255.0).contains(&h) || s < 0.35 || v < 0.2 {
            continue;
        }
        let (h, s, v) = match waves {
            Waves::Orange => (h - 190.0, s * 0.95, v),
            Waves::Grey => (h, s * 0.12, v * 0.78),
            // Shift by the same amount everywhere so the logo's light-to-dark gradient survives.
            Waves::Green => (h - 85.0, s * 0.95, v * 0.92),
        };
        let (r, g, b) = from_hsv(h, s, v);
        p.0[..3].copy_from_slice(&[r, g, b]);
    }
    out
}

fn to_hsv(r: u8, g: u8, b: u8) -> (f32, f32, f32) {
    let (r, g, b) = (r as f32 / 255.0, g as f32 / 255.0, b as f32 / 255.0);
    let max = r.max(g).max(b);
    let delta = max - r.min(g).min(b);
    let h = if delta == 0.0 {
        0.0
    } else if max == r {
        60.0 * ((g - b) / delta).rem_euclid(6.0)
    } else if max == g {
        60.0 * ((b - r) / delta + 2.0)
    } else {
        60.0 * ((r - g) / delta + 4.0)
    };
    (h, if max == 0.0 { 0.0 } else { delta / max }, max)
}

fn from_hsv(h: f32, s: f32, v: f32) -> (u8, u8, u8) {
    let c = v * s;
    let h = h.rem_euclid(360.0) / 60.0;
    let x = c * (1.0 - (h % 2.0 - 1.0).abs());
    let (r, g, b) = match h as u32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    let m = v - c;
    let to = |u: f32| ((u + m) * 255.0).round().clamp(0.0, 255.0) as u8;
    (to(r), to(g), to(b))
}

/// The graphic on a dark rounded square with a soft top-down gradient.
fn app_icon(graphic: &RgbaImage, side: u32) -> RgbaImage {
    let s = side as f32;
    let radius = s * 0.225;
    let mut out = RgbaImage::new(side, side);
    for (x, y, p) in out.enumerate_pixels_mut() {
        let (fx, fy) = (x as f32 + 0.5, y as f32 + 0.5);
        // Distance outside the rounded rectangle (negative inside).
        let qx = (fx - s / 2.0).abs() - (s / 2.0 - radius);
        let qy = (fy - s / 2.0).abs() - (s / 2.0 - radius);
        let outside = (qx.max(0.0).powi(2) + qy.max(0.0).powi(2)).sqrt() + qx.max(qy).min(0.0) - radius;
        let coverage = (0.5 - outside).clamp(0.0, 1.0);
        if coverage > 0.0 {
            let t = fy / s;
            let shade = |top: f32, bottom: f32| (top + (bottom - top) * t) as u8;
            *p = Rgba([shade(38.0, 14.0), shade(38.0, 14.0), shade(46.0, 18.0), (coverage * 255.0) as u8]);
        }
    }
    let inner = (s * 0.78) as u32;
    let art = resize(graphic, inner);
    let offset = ((side - inner) / 2) as i64;
    imageops::overlay(&mut out, &art, offset, offset);
    out
}

fn write_ico(app: &RgbaImage, path: &Path) {
    use image::ExtendedColorType;
    use image::codecs::ico::{IcoEncoder, IcoFrame};
    let frames: Vec<IcoFrame> = [16, 24, 32, 48, 64, 128, 256]
        .iter()
        .map(|&s| {
            let img = resize(app, s);
            IcoFrame::as_png(img.as_raw(), s, s, ExtendedColorType::Rgba8).unwrap()
        })
        .collect();
    let file = std::fs::File::create(path).unwrap();
    IcoEncoder::new(file).encode_images(&frames).unwrap();
}

fn write_icns(app: &RgbaImage, path: &Path) {
    let mut family = icns::IconFamily::new();
    for s in [16, 32, 64, 128, 256, 512, 1024] {
        let img = resize(app, s);
        let icon = icns::Image::from_data(icns::PixelFormat::RGBA, s, s, img.into_raw()).unwrap();
        family.add_icon(&icon).unwrap();
    }
    family.write(std::fs::File::create(path).unwrap()).unwrap();
}
