"""Global hotkeys.

Wayland does not let a background app grab keys, so the desktop does it for us: on GNOME
the app registers two custom keyboard shortcuts (Settings > Keyboard) whose command is
`dynamite-tts speak` and `dynamite-tts toggle-mode`. Those commands hand the request to
the running app over its control socket. Other desktops bind the same commands by hand.
"""
from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path

from . import applog

MEDIA_KEYS_SCHEMA = "org.gnome.settings-daemon.plugins.media-keys"
CUSTOM_SCHEMA = f"{MEDIA_KEYS_SCHEMA}.custom-keybinding"
BASE_PATH = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/"
SPEAK_PATH = f"{BASE_PATH}dynamite-tts-speak/"
MODE_PATH = f"{BASE_PATH}dynamite-tts-mode/"

MODIFIER_WORDS = {
    "<control>": "Ctrl",
    "<primary>": "Ctrl",
    "<shift>": "Shift",
    "<alt>": "Alt",
    "<super>": "Super",
    "<meta>": "Meta",
    "<hyper>": "Hyper",
}


def launcher_command() -> str:
    """Absolute command GNOME should run for a hotkey."""
    override = os.environ.get("DYNAMITE_TTS_COMMAND")
    if override:
        return override
    installed = shutil.which("dynamite-tts")
    if installed:
        return installed
    local = Path.home() / ".local" / "bin" / "dynamite-tts"
    if local.is_file() and os.access(local, os.X_OK):
        return str(local)
    repo_launcher = Path(__file__).resolve().parent.parent / "dynamite-tts"
    if repo_launcher.is_file():
        return str(repo_launcher)
    return f"{sys.executable} -m dynamite_tts"


def display_name(accelerator: str) -> str:
    """Human-readable form of a GTK accelerator, e.g. '<Control><Shift>s' -> 'Ctrl+Shift+S'."""
    rest = accelerator.strip()
    parts: list[str] = []
    while rest.startswith("<"):
        end = rest.find(">")
        if end < 0:
            break
        word = MODIFIER_WORDS.get(rest[: end + 1].lower(), rest[1:end])
        if word not in parts:
            parts.append(word)
        rest = rest[end + 1 :]
    key = rest
    if len(key) == 1:
        key = key.upper()
    elif key:
        key = key[0].upper() + key[1:]
    parts.append(key or "?")
    return "+".join(parts)


class HotkeyBinder:
    """Writes the two custom keybindings into GNOME's gsettings. No-op elsewhere."""

    def __init__(self):
        self._settings = None
        self._schema_source = None
        self._bound: tuple[str, str, str] | None = None
        try:
            import gi

            gi.require_version("Gio", "2.0")
            from gi.repository import Gio

            self._gio = Gio
            source = Gio.SettingsSchemaSource.get_default()
            if source is None or source.lookup(MEDIA_KEYS_SCHEMA, True) is None or source.lookup(CUSTOM_SCHEMA, True) is None:
                return
            self._schema_source = source
            self._settings = Gio.Settings.new(MEDIA_KEYS_SCHEMA)
        except (ImportError, ValueError):
            self._gio = None

    @property
    def available(self) -> bool:
        return self._settings is not None

    def bind(self, speak: str, mode: str) -> None:
        if not self.available:
            applog.write("hotkeys: GNOME media-keys schema missing; bind the commands in your desktop's keyboard settings")
            return
        command = launcher_command()
        if self._bound == (command, speak, mode):
            return
        self._bound = (command, speak, mode)
        self._write(SPEAK_PATH, "Dynamite TTS: Speak selection", f"{command} speak", speak)
        self._write(MODE_PATH, "Dynamite TTS: Toggle mode", f"{command} toggle-mode", mode)
        paths = list(self._settings.get_strv("custom-keybindings"))
        changed = False
        for path in (SPEAK_PATH, MODE_PATH):
            if path not in paths:
                paths.append(path)
                changed = True
        if changed:
            self._settings.set_strv("custom-keybindings", paths)
        self._gio.Settings.sync()
        applog.write(f"hotkeys bound speak={speak} mode={mode} via GNOME custom keybindings")

    def unbind(self) -> None:
        if not self.available:
            return
        paths = [p for p in self._settings.get_strv("custom-keybindings") if p not in (SPEAK_PATH, MODE_PATH)]
        self._settings.set_strv("custom-keybindings", paths)
        for path in (SPEAK_PATH, MODE_PATH):
            entry = self._gio.Settings.new_with_path(CUSTOM_SCHEMA, path)
            for key in ("name", "command", "binding"):
                entry.reset(key)
        self._gio.Settings.sync()

    def _write(self, path: str, name: str, command: str, binding: str) -> None:
        entry = self._gio.Settings.new_with_path(CUSTOM_SCHEMA, path)
        if entry.get_string("name") != name:
            entry.set_string("name", name)
        if entry.get_string("command") != command:
            entry.set_string("command", command)
        if entry.get_string("binding") != binding:
            entry.set_string("binding", binding)
