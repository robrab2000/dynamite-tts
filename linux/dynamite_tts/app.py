"""The tray application: indicator menu, phases, hotkey commands, and settings."""
from __future__ import annotations

import os
import signal
import sys
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk  # noqa: E402

from . import APP_NAME, applog, autostart, selection  # noqa: E402
from .control import ControlServer  # noqa: E402
from .hotkeys import HotkeyBinder, display_name, launcher_command  # noqa: E402
from .settings import SettingsStore, SpeakMode  # noqa: E402
from .speech import SpeechSession  # noqa: E402
from .status import StatusNotifier  # noqa: E402

ICON_DIR = Path(__file__).resolve().parent.parent / "icons"
ICONS = {None: "dynamite-tts-idle", "Speaking": "dynamite-tts-speaking"}

try:
    gi.require_version("AyatanaAppIndicator3", "0.1")
    from gi.repository import AyatanaAppIndicator3 as AppIndicator

    _HAVE_INDICATOR = True
except (ImportError, ValueError):
    try:
        gi.require_version("AppIndicator3", "0.1")
        from gi.repository import AppIndicator3 as AppIndicator

        _HAVE_INDICATOR = True
    except (ImportError, ValueError):
        AppIndicator = None
        _HAVE_INDICATOR = False


def _icon_for(phase: str | None) -> str:
    return ICONS.get(phase, "dynamite-tts-active")


class DynamiteApp:
    def __init__(self, speak_text: str | None = None):
        self.store = SettingsStore()
        self.speech = SpeechSession(self.store)
        self.speech.on_phase = self.show_phase
        self.speech.on_notice = self.show_notice
        self.status = StatusNotifier(on_stop=lambda: self.speech.stop(play_chime=True), icon_name=str(ICON_DIR / "dynamite-tts-idle.png"))
        self.hotkeys = HotkeyBinder()
        self.control = ControlServer(self.handle_command)
        self.settings_window = None
        self.indicator = None
        self.mode_item = None
        self._speak_text = speak_text
        self._phase: str | None = None

    # -- lifecycle ---------------------------------------------------------------
    def run(self) -> int:
        GLib.set_prgname("dynamite-tts")
        GLib.set_application_name(APP_NAME)
        self.control.start()
        self._build_indicator()
        self.apply_settings()
        self._warn_about_tools()
        if self._speak_text:
            GLib.idle_add(lambda: self.speech.speak(self._speak_text) or False)
        for sig in (signal.SIGINT, signal.SIGTERM):
            GLib.unix_signal_add(GLib.PRIORITY_HIGH, sig, self._quit_signal)
        applog.write(f"ready hotkeys speak={display_name(self.store.current.speak_hotkey)} mode={display_name(self.store.current.mode_hotkey)} indicator={_HAVE_INDICATOR}")
        Gtk.main()
        return 0

    def quit(self) -> None:
        applog.write("quit")
        self.speech.stop(play_chime=False)
        self.status.hide()
        self.control.stop()
        Gtk.main_quit()

    def _quit_signal(self) -> bool:
        self.quit()
        return False

    # -- tray --------------------------------------------------------------------
    def _build_indicator(self) -> None:
        menu = Gtk.Menu()
        stop = Gtk.MenuItem(label="Stop")
        stop.connect("activate", lambda *_: self.speech.stop(play_chime=True))
        menu.append(stop)
        self.mode_item = Gtk.MenuItem(label="Mode: Verbatim")
        self.mode_item.connect("activate", lambda *_: self.cycle_mode())
        menu.append(self.mode_item)
        menu.append(Gtk.SeparatorMenuItem())
        settings = Gtk.MenuItem(label="Settings…")
        settings.connect("activate", lambda *_: self.open_settings())
        menu.append(settings)
        quit_item = Gtk.MenuItem(label="Quit")
        quit_item.connect("activate", lambda *_: self.quit())
        menu.append(quit_item)
        menu.show_all()
        self._menu = menu

        if not _HAVE_INDICATOR:
            applog.write("tray unavailable: install gir1.2-ayatanaappindicator3-0.1 (and the AppIndicator GNOME extension)")
            return
        self.indicator = AppIndicator.Indicator.new_with_path(
            "dynamite-tts", _icon_for(None), AppIndicator.IndicatorCategory.APPLICATION_STATUS, str(ICON_DIR)
        )
        self.indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
        self.indicator.set_title(APP_NAME)
        self.indicator.set_menu(menu)
        self.indicator.set_secondary_activate_target(stop)

    # -- settings ----------------------------------------------------------------
    def apply_settings(self) -> None:
        current = self.store.current
        self.hotkeys.bind(current.speak_hotkey, current.mode_hotkey)
        if self.mode_item is not None:
            self.mode_item.set_label(f"Mode: {current.speak_mode.title}")
        try:
            autostart.apply(current.launch_at_login)
        except OSError as error:
            applog.write(f"autostart failed: {error}")

    def set_mode(self, mode: SpeakMode) -> None:
        self.store.update(lambda s: setattr(s, "speak_mode", mode))
        self.apply_settings()
        self.show_notice(f"Mode: {mode.title}")

    def cycle_mode(self) -> None:
        self.set_mode(self.store.current.speak_mode.next())

    def open_settings(self) -> None:
        from .settings_window import SettingsWindow

        if self.settings_window is None:
            self.settings_window = SettingsWindow(self)
            self.settings_window.connect("destroy", self._settings_closed)
        self.settings_window.present()

    def _settings_closed(self, *_) -> None:
        self.settings_window = None

    # -- speaking ----------------------------------------------------------------
    def speak_selection(self) -> None:
        text = selection.capture()
        if not text:
            self.show_notice("No text selected")
            return
        self.speech.speak(text)

    def show_phase(self, phase: str | None) -> None:
        self._phase = phase
        title = phase or "Ready"
        applog.write(f"phase: {title}")
        if self.indicator is not None:
            self.indicator.set_icon_full(_icon_for(phase), f"{APP_NAME} ({title})")
        if self.settings_window is not None:
            self.settings_window.set_status(title)
        if not self.store.current.show_status_notifications:
            self.status.hide()
            return
        if phase:
            self.status.show(phase, persistent=True)
        else:
            self.status.hide()

    def show_notice(self, text: str) -> None:
        applog.write(f"notice: {text}")
        if self.settings_window is not None:
            self.settings_window.set_status(text)
        if self.store.current.show_status_notifications:
            self.status.show_notice(text)

    # -- control socket ----------------------------------------------------------
    def handle_command(self, command: str) -> str:
        if command == "speak":
            self.speak_selection()
        elif command == "toggle-mode":
            self.cycle_mode()
        elif command == "stop":
            self.speech.stop(play_chime=True)
        elif command == "settings":
            self.open_settings()
        elif command == "quit":
            GLib.idle_add(lambda: self.quit() or False)
        elif command == "ping":
            return (f"ok phase={self._phase or 'Ready'} mode={self.store.current.speak_mode.value} "
                    f"settings={'open' if self.settings_window is not None else 'closed'}")
        elif command.startswith("speak-text "):
            self.speech.speak(command[len("speak-text "):])
        else:
            return f"error: unknown command {command!r}"
        return "ok"

    def _warn_about_tools(self) -> None:
        missing = selection.missing_tools()
        if missing:
            self.show_notice(f"Install {', '.join(missing)} so the highlight can be read")
        if not self.hotkeys.available:
            applog.write(
                "bind these commands in your desktop's keyboard shortcuts: "
                f"'{launcher_command()} speak' and '{launcher_command()} toggle-mode'"
            )
