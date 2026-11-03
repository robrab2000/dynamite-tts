//! Regenerates the app and tray icons from the logo:
//!
//!     cargo run --example make_icons [assets/logo.png]
//!
//! The logo's wordmark is unreadable at icon sizes, so the icons use only its graphic (the
//! dynamite and the speech bubble), cut out of the black background:
//!
//! - assets/icons/{idle,active,speaking}.png: the tray icon on transparency, 128 px, with a
//!   bolder fuse so it survives menu-bar size. The bubble's waveform and sound waves show the
//!   state (grey when ready, orange while working, the logo's blue while speaking) and the
//!   fuse is lit only while working or speaking.
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
    /// Speaking: the logo's own blue.
    Blue,
}

fn main() {
    let source = std::env::args().nth(1).unwrap_or_else(|| "assets/logo.png".into());
    let logo = image::open(&source).unwrap_or_else(|e| panic!("{source}: {e}")).into_rgba8();
    assert_eq!(logo.dimensions(), (2000, 2000), "GRAPHIC is measured on the 2000 px logo");
    let (x, y, w, h) = GRAPHIC;
    let graphic = trim(&cut_out(&imageops::crop_imm(&logo, x, y, w, h).to_image()));
    let out = Path::new("assets/icons");

    // At menu-bar size the logo's fuse is a hairline that vanishes, so the tray draws it bolder.
    // The spark is removed at full resolution, before shrinking, so its edges are clean.
    let base = pad(&graphic, 0.04);
    let fuse = fit_fuse(&base);
    let lit = draw_fuse(&base, &fuse);
    let out_cold = draw_fuse(&unlit(&base), &fuse);
    let preview = Path::new("target/icon-preview");
    std::fs::create_dir_all(preview).unwrap();
    for (name, base, waves) in
        [("idle", &out_cold, Waves::Grey), ("active", &lit, Waves::Orange), ("speaking", &lit, Waves::Blue)]
    {
        recolor_waves(&resize(base, 128), waves).save(out.join(format!("{name}.png"))).unwrap();
        recolor_waves(&resize(base, 512), waves).save(preview.join(format!("{name}.png"))).unwrap();
    }
    menu_bar_preview(&[(&out_cold, Waves::Grey), (&lit, Waves::Orange), (&lit, Waves::Blue)])
        .save(preview.join("menu-bar.png"))
        .unwrap();

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

/// The spark's centre: the middle of its bright yellow core, in the top-right corner.
fn spark_centre(img: &RgbaImage) -> (f32, f32) {
    let (w, h) = img.dimensions();
    let (mut sx, mut sy, mut n) = (0.0, 0.0, 0.0);
    for (x, y, p) in img.enumerate_pixels() {
        let corner = x as f32 > w as f32 * 0.6 && (y as f32) < h as f32 * 0.42;
        let (hue, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
        if corner && p.0[3] > 128 && (35.0..=65.0).contains(&hue) && s > 0.5 && v > 0.85 {
            (sx, sy, n) = (sx + x as f32, sy + y as f32, n + 1.0);
        }
    }
    assert!(n > 0.0, "no spark found");
    (sx / n, sy / n)
}

/// Pixels within `radius` of `points`, as a mask over the image.
fn dilate(points: &[(i32, i32)], radius: i32, w: u32, h: u32) -> Vec<bool> {
    let mut mask = vec![false; (w * h) as usize];
    for &(px, py) in points {
        for dy in -radius..=radius {
            for dx in -radius..=radius {
                let (x, y) = (px + dx, py + dy);
                if dx * dx + dy * dy <= radius * radius && x >= 0 && y >= 0 && (x as u32) < w && (y as u32) < h {
                    mask[(y as u32 * w + x as u32) as usize] = true;
                }
            }
        }
    }
    mask
}

/// The fuse's dark, unsaturated body: near the spark (but not its white-hot tip) and up to the
/// dynamite's cap.
fn fuse_body(img: &RgbaImage, (cx, cy): (f32, f32)) -> Vec<(i32, i32)> {
    let w = img.width() as f32;
    img.enumerate_pixels()
        .filter(|&(x, y, p)| {
            let d = ((x as f32 - cx).powi(2) + (y as f32 - cy).powi(2)).sqrt();
            let (_, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
            d <= w * 0.26 && d >= w * 0.045 && p.0[3] > 200 && v < 0.45 && s < 0.45
        })
        .map(|(x, y, _)| (x as i32, y as i32))
        .collect()
}

/// Redraws the fuse as a smooth, thicker stroke (mid-grey with a light edge) so it reads at
/// 22 px on dark and light menu bars alike. A quadratic curve is fitted through the logo's fuse:
/// from its cap end, through its middle, to the tip under the spark. The stroke goes under the
/// spark and behind the dynamite's red cap.
/// Where the spark sits and the path its fuse takes, measured on the original logo.
struct Fuse {
    centre: (f32, f32),
    curve: Vec<(f32, f32)>,
}

fn fit_fuse(img: &RgbaImage) -> Fuse {
    let w = img.width();
    let (cx, cy) = spark_centre(img);
    let mut body: Vec<(f32, f32, f32)> = fuse_body(img, (cx, cy))
        .into_iter()
        .map(|(x, y)| {
            let (x, y) = (x as f32, y as f32);
            (x, y, ((x - cx).powi(2) + (y - cy).powi(2)).sqrt())
        })
        .collect();
    assert!(body.len() > 50, "fuse not found");
    body.sort_by(|a, b| a.2.total_cmp(&b.2));
    // Percentiles rather than extremes, so stray dark pixels don't bend the curve.
    let at = |q: f32| {
        let (x, y, _) = body[((body.len() - 1) as f32 * q) as usize];
        (x, y)
    };
    let (tip, cap) = (at(0.03), at(0.97));
    let mid_ring: Vec<_> =
        body.iter().filter(|p| (p.2 - (body[0].2 + body.last().unwrap().2) / 2.0).abs() < w as f32 * 0.01).collect();
    let mid = (
        mid_ring.iter().map(|p| p.0).sum::<f32>() / mid_ring.len().max(1) as f32,
        mid_ring.iter().map(|p| p.1).sum::<f32>() / mid_ring.len().max(1) as f32,
    );
    // Control point so the curve passes through `mid` halfway along.
    let ctrl = (2.0 * mid.0 - (tip.0 + cap.0) / 2.0, 2.0 * mid.1 - (tip.1 + cap.1) / 2.0);
    let curve: Vec<(f32, f32)> = (0..=240)
        .map(|i| {
            let t = i as f32 / 240.0;
            let (a, b, c) = ((1.0 - t) * (1.0 - t), 2.0 * t * (1.0 - t), t * t);
            (a * cap.0 + b * ctrl.0 + c * tip.0, a * cap.1 + b * ctrl.1 + c * tip.1)
        })
        .collect();
    Fuse { centre: (cx, cy), curve }
}

fn draw_fuse(img: &RgbaImage, fuse: &Fuse) -> RgbaImage {
    let (w, h) = img.dimensions();
    let ((cx, cy), curve) = (fuse.centre, &fuse.curve);
    let (core, edge) = (w as f32 * 0.021, w as f32 * 0.034);
    let (x0, x1) = (
        curve.iter().map(|p| p.0).fold(f32::MAX, f32::min) - edge,
        curve.iter().map(|p| p.0).fold(0.0, f32::max) + edge,
    );
    let (y0, y1) = (
        curve.iter().map(|p| p.1).fold(f32::MAX, f32::min) - edge,
        curve.iter().map(|p| p.1).fold(0.0, f32::max) + edge,
    );
    let mut out = img.clone();
    for y in (y0.max(0.0) as u32)..(y1.min(h as f32 - 1.0) as u32) {
        for x in (x0.max(0.0) as u32)..(x1.min(w as f32 - 1.0) as u32) {
            let (fx, fy) = (x as f32 + 0.5, y as f32 + 0.5);
            let d = curve.iter().map(|&(px, py)| (px - fx).powi(2) + (py - fy).powi(2)).fold(f32::MAX, f32::min).sqrt();
            let (edge_cov, core_cov) = ((edge + 0.5 - d).clamp(0.0, 1.0), (core + 0.5 - d).clamp(0.0, 1.0));
            if edge_cov <= 0.0 {
                continue;
            }
            let p = out.get_pixel_mut(x, y);
            let (hue, s, v) = to_hsv(p.0[0], p.0[1], p.0[2]);
            let opaque = p.0[3] > 160;
            let red = opaque && (hue <= 20.0 || hue >= 340.0) && s > 0.45 && v > 0.25;
            let spark = p.0[3] > 0 && (25.0..=75.0).contains(&hue) && s > 0.35 && v > 0.5;
            let hot =
                p.0[3] > 0 && v > 0.9 && s < 0.25 && ((fx - cx).powi(2) + (fy - cy).powi(2)).sqrt() < w as f32 * 0.05;
            if red || spark || hot {
                continue;
            }
            over(p, [0xee, 0xee, 0xf2], edge_cov);
            over(p, [0x74, 0x74, 0x80], core_cov);
        }
    }
    out
}

/// Paints `color` over `p` with coverage `alpha` (source over).
fn over(p: &mut Rgba<u8>, color: [u8; 3], alpha: f32) {
    if alpha <= 0.0 {
        return;
    }
    let dst_a = p.0[3] as f32 / 255.0;
    let out_a = alpha + dst_a * (1.0 - alpha);
    for (channel, &src) in p.0[..3].iter_mut().zip(&color) {
        *channel = ((src as f32 * alpha + *channel as f32 * dst_a * (1.0 - alpha)) / out_a).round() as u8;
    }
    p.0[3] = (out_a * 255.0).round() as u8;
}

/// Puts the spark out for the ready icon. Around the spark only the fuse survives: its body
/// plus anything within an edge's width of it. The star, its outline, glow and embers go.
fn unlit(img: &RgbaImage) -> RgbaImage {
    let (w, h) = img.dimensions();
    let (cx, cy) = spark_centre(img);
    let keep = dilate(&fuse_body(img, (cx, cy)), (w as f32 * 0.035).ceil() as i32, w, h);
    let reach = w as f32 * 0.19;
    let mut out = img.clone();
    for (x, y, p) in out.enumerate_pixels_mut() {
        if ((x as f32 - cx).powi(2) + (y as f32 - cy).powi(2)).sqrt() > reach {
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
            // Shift by the same amount everywhere so the logo's light-to-dark gradient survives.
            Waves::Orange => (h - 190.0, s * 0.95, v),
            Waves::Grey => (h, s * 0.12, v * 0.78),
            Waves::Blue => continue,
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

/// Each state at 22 px and 44 px (Retina) on olive, dark and light menu bars, enlarged 3x
/// with nearest-neighbour so single pixels stay visible.
fn menu_bar_preview(states: &[(&RgbaImage, Waves)]) -> RgbaImage {
    let bars = [[0x6b, 0x6a, 0x2a], [0x1e, 0x1e, 0x20], [0xec, 0xec, 0xec]];
    let (cell, gap) = (48u32, 8u32);
    let width = (states.len() as u32 * 2) * (cell + gap) + gap;
    let mut strip = RgbaImage::new(width, bars.len() as u32 * (cell + gap) + gap);
    for (row, bar) in bars.iter().enumerate() {
        let y = gap + row as u32 * (cell + gap);
        for x in 0..width {
            for dy in 0..cell {
                strip.put_pixel(x, y + dy, Rgba([bar[0], bar[1], bar[2], 255]));
            }
        }
        for (i, (base, waves)) in states.iter().enumerate() {
            for (j, size) in [22u32, 44].into_iter().enumerate() {
                let icon = recolor_waves(&resize(base, size), *waves);
                let x = gap + ((i * 2 + j) as u32) * (cell + gap) + (cell - size) / 2;
                imageops::overlay(&mut strip, &icon, x as i64, (y + (cell - size) / 2) as i64);
            }
        }
    }
    imageops::resize(&strip, strip.width() * 3, strip.height() * 3, imageops::FilterType::Nearest)
}
