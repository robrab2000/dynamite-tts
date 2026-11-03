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

const WORKING: [u8; 3] = [0xff, 0x9f, 0x0a];
const SPEAKING: [u8; 3] = [0x30, 0xd1, 0x58];

fn main() {
    let source = std::env::args().nth(1).unwrap_or_else(|| "assets/logo.png".into());
    let logo = image::open(&source).unwrap_or_else(|e| panic!("{source}: {e}")).into_rgba8();
    assert_eq!(logo.dimensions(), (2000, 2000), "GRAPHIC is measured on the 2000 px logo");
    let (x, y, w, h) = GRAPHIC;
    let graphic = trim(&cut_out(&imageops::crop_imm(&logo, x, y, w, h).to_image()));
    let out = Path::new("assets/icons");

    let tray = resize(&pad(&graphic, 0.04), 128);
    tray.save(out.join("idle.png")).unwrap();
    with_badge(&tray, WORKING).save(out.join("active.png")).unwrap();
    with_badge(&tray, SPEAKING).save(out.join("speaking.png")).unwrap();

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

/// Source-over blend of `color` with coverage `alpha` onto `p`.
fn blend(p: &mut Rgba<u8>, color: [u8; 3], alpha: f32) {
    let src_a = alpha;
    let dst_a = p.0[3] as f32 / 255.0;
    let out_a = src_a + dst_a * (1.0 - src_a);
    if out_a <= 0.0 {
        return;
    }
    for (channel, &src) in p.0[..3].iter_mut().zip(&color) {
        let v = (src as f32 * src_a + *channel as f32 * dst_a * (1.0 - src_a)) / out_a;
        *channel = v.round() as u8;
    }
    p.0[3] = (out_a * 255.0).round() as u8;
}

/// A status dot in the top-left corner (the graphic leaves it empty), with a dark ring so
/// it reads on light and dark menu bars.
fn with_badge(img: &RgbaImage, color: [u8; 3]) -> RgbaImage {
    let mut out = img.clone();
    let side = img.width() as f32;
    let (cx, cy, r) = (side * 0.17, side * 0.17, side * 0.15);
    for (x, y, p) in out.enumerate_pixels_mut() {
        let d = ((x as f32 + 0.5 - cx).powi(2) + (y as f32 + 0.5 - cy).powi(2)).sqrt();
        let ring = (r + 0.5 - d).clamp(0.0, 1.0);
        if ring > 0.0 {
            blend(p, [0x14, 0x14, 0x18], ring);
        }
        let dot = (r * 0.74 + 0.5 - d).clamp(0.0, 1.0);
        if dot > 0.0 {
            blend(p, color, dot);
        }
    }
    out
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
