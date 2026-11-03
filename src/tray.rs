//! The tray (menu-bar) icon and its menu: Stop, Mode, Settings…, Quit. The icon shows whether
//! the app is ready, working, or speaking.

use tray_icon::menu::{Menu, MenuId, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder};

use crate::settings::SpeakMode;

const IDLE_PNG: &[u8] = include_bytes!("../assets/icons/idle.png");
const ACTIVE_PNG: &[u8] = include_bytes!("../assets/icons/active.png");
const SPEAKING_PNG: &[u8] = include_bytes!("../assets/icons/speaking.png");

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Look {
    Idle,
    Active,
    Speaking,
}

pub fn decode(png: &[u8]) -> Option<(Vec<u8>, u32, u32)> {
    let image = image::load_from_memory_with_format(png, image::ImageFormat::Png).ok()?.into_rgba8();
    let (w, h) = image.dimensions();
    Some((image.into_raw(), w, h))
}

fn icon(look: Look) -> Option<Icon> {
    let png = match look {
        Look::Idle => IDLE_PNG,
        Look::Active => ACTIVE_PNG,
        Look::Speaking => SPEAKING_PNG,
    };
    let (rgba, w, h) = decode(png)?;
    Icon::from_rgba(rgba, w, h).ok()
}

/// The app icon as a PNG file on disk (for Linux notifications), written once.
#[allow(dead_code)]
pub fn icon_file() -> Option<std::path::PathBuf> {
    let path = crate::paths::data_dir().join("icons").join("dynamite-tts.png");
    if !path.is_file() {
        std::fs::create_dir_all(path.parent()?).ok()?;
        std::fs::write(&path, IDLE_PNG).ok()?;
    }
    Some(path)
}

pub struct Tray {
    icon: TrayIcon,
    look: Look,
    mode: MenuItem,
    pub stop_id: MenuId,
    pub mode_id: MenuId,
    pub settings_id: MenuId,
    pub quit_id: MenuId,
}

impl Tray {
    pub fn new(mode: SpeakMode) -> Result<Tray, String> {
        let stop = MenuItem::new("Stop", true, None);
        let mode_item = MenuItem::new(mode_label(mode), true, None);
        let settings = MenuItem::new("Settings…", true, None);
        let quit = MenuItem::new("Quit Dynamite TTS", true, None);
        let menu = Menu::new();
        menu.append_items(&[&stop, &mode_item, &PredefinedMenuItem::separator(), &settings, &quit])
            .map_err(|e| e.to_string())?;
        let mut builder = TrayIconBuilder::new().with_menu(Box::new(menu)).with_tooltip("Dynamite TTS (Ready)");
        if let Some(i) = icon(Look::Idle) {
            builder = builder.with_icon(i);
        }
        let icon = builder.build().map_err(|e| e.to_string())?;
        Ok(Tray {
            icon,
            look: Look::Idle,
            stop_id: stop.id().clone(),
            mode_id: mode_item.id().clone(),
            settings_id: settings.id().clone(),
            quit_id: quit.id().clone(),
            mode: mode_item,
        })
    }

    pub fn set_phase(&mut self, phase: Option<&str>) {
        let look = match phase {
            None => Look::Idle,
            Some("Speaking") => Look::Speaking,
            Some(_) => Look::Active,
        };
        if look != self.look {
            self.look = look;
            let _ = self.icon.set_icon(icon(look));
        }
        let _ = self.icon.set_tooltip(Some(format!("Dynamite TTS ({})", phase.unwrap_or("Ready"))));
    }

    pub fn set_mode(&self, mode: SpeakMode) {
        self.mode.set_text(mode_label(mode));
    }
}

fn mode_label(mode: SpeakMode) -> String {
    format!("Mode: {} (click to change)", mode.title())
}

#[cfg(test)]
mod tests {
    #[test]
    fn the_icons_decode() {
        for png in [super::IDLE_PNG, super::ACTIVE_PNG, super::SPEAKING_PNG] {
            let (rgba, w, h) = super::decode(png).unwrap();
            assert_eq!(rgba.len(), (w * h * 4) as usize);
        }
    }
}
