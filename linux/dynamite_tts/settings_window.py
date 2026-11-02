"""GTK settings window."""
from __future__ import annotations

import threading

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from . import APP_NAME, applog  # noqa: E402
from .hotkeys import display_name, launcher_command  # noqa: E402
from .lemonade import LemonadeClient  # noqa: E402
from .playback import list_output_devices  # noqa: E402
from . import phonon  # noqa: E402
from .settings import PHONON_LANGUAGES, VOICES, Engine, ExplainAudience, SpeakMode, phonon_voices_for  # noqa: E402

PREVIEW_TEXT = "Hi, this is Dynamite TTS. Highlight some text and press the shortcut to hear it read aloud."


class HotkeyButton(Gtk.Button):
    """Press to record a shortcut; the next key chord becomes the accelerator."""

    def __init__(self, accelerator: str, on_change):
        super().__init__()
        self.accelerator = accelerator
        self._on_change = on_change
        self._recording = False
        self._refresh()
        self.connect("clicked", self._start)
        self.connect("key-press-event", self._key)
        self.connect("focus-out-event", self._cancel)

    def _refresh(self) -> None:
        self.set_label("Press shortcut…" if self._recording else display_name(self.accelerator))

    def _start(self, *_) -> None:
        self._recording = True
        self._refresh()
        self.grab_focus()

    def _cancel(self, *_) -> bool:
        if self._recording:
            self._recording = False
            self._refresh()
        return False

    def _key(self, _widget, event) -> bool:
        if not self._recording:
            return False
        keyval = event.keyval
        if keyval == Gdk.KEY_Escape:
            self._cancel()
            return True
        if keyval in (Gdk.KEY_Shift_L, Gdk.KEY_Shift_R, Gdk.KEY_Control_L, Gdk.KEY_Control_R,
                      Gdk.KEY_Alt_L, Gdk.KEY_Alt_R, Gdk.KEY_Super_L, Gdk.KEY_Super_R, Gdk.KEY_Meta_L, Gdk.KEY_Meta_R):
            return True
        mods = event.state & Gtk.accelerator_get_default_mod_mask()
        if not mods:
            return True  # a bare key would fire while typing; require a modifier
        lower = Gdk.keyval_to_lower(keyval)
        accelerator = Gtk.accelerator_name(lower, mods)
        self._recording = False
        self.accelerator = accelerator
        self._refresh()
        self._on_change(accelerator)
        return True


class SettingsWindow(Gtk.Window):
    def __init__(self, app):
        super().__init__(title=f"{APP_NAME} Settings")
        self.app = app
        self.store = app.store
        self.set_default_size(520, -1)
        self.set_border_width(16)
        self.set_position(Gtk.WindowPosition.CENTER)
        self._client = LemonadeClient()
        self._build()

    def _row(self, grid: Gtk.Grid, row: int, label: str, widget: Gtk.Widget) -> None:
        text = Gtk.Label(label=label, xalign=1.0)
        text.get_style_context().add_class("dim-label")
        grid.attach(text, 0, row, 1, 1)
        widget.set_hexpand(True)
        grid.attach(widget, 1, row, 1, 1)

    def _build(self) -> None:
        current = self.store.current
        grid = Gtk.Grid(row_spacing=10, column_spacing=12)
        row = 0

        # Shortcuts
        self._row(grid, row, "Speak shortcut", HotkeyButton(current.speak_hotkey, lambda a: self._set("speak_hotkey", a)))
        row += 1
        self._row(grid, row, "Mode shortcut", HotkeyButton(current.mode_hotkey, lambda a: self._set("mode_hotkey", a)))
        row += 1
        if not self.app.hotkeys.available:
            hint = Gtk.Label(xalign=0, wrap=True)
            hint.set_markup(
                "This desktop is not GNOME, so bind these commands yourself in its keyboard settings:\n"
                f"<tt>{GLib.markup_escape_text(launcher_command())} speak</tt>\n"
                f"<tt>{GLib.markup_escape_text(launcher_command())} toggle-mode</tt>"
            )
            grid.attach(hint, 0, row, 2, 1)
            row += 1

        # Mode
        mode = Gtk.ComboBoxText()
        for item in SpeakMode:
            mode.append(item.value, item.title)
        mode.set_active_id(current.speak_mode.value)
        mode.connect("changed", lambda c: self._set("speak_mode", SpeakMode(c.get_active_id())))
        self._row(grid, row, "Speak mode", mode)
        row += 1

        # Engine
        engine = Gtk.ComboBoxText()
        for item in Engine:
            engine.append(item.value, item.title)
        engine.set_active_id(current.engine.value)
        engine.connect("changed", self._on_engine_changed)
        self._row(grid, row, "Engine", engine)
        row += 1
        self._engine_hint = Gtk.Label(xalign=0, wrap=True)
        self._engine_hint.get_style_context().add_class("dim-label")
        grid.attach(self._engine_hint, 1, row, 1, 1)
        row += 1

        # Phonon text language (shown for Phonon)
        self._language = Gtk.ComboBoxText()
        for code, label in PHONON_LANGUAGES:
            self._language.append(code, label)
        if self._language.set_active_id(current.phonon_language) is False:
            self._language.append(current.phonon_language, current.phonon_language)
            self._language.set_active_id(current.phonon_language)
        self._language.connect("changed", self._on_language_changed)
        self._language_label = Gtk.Label(label="Text language", xalign=1.0)
        self._language_label.get_style_context().add_class("dim-label")
        grid.attach(self._language_label, 0, row, 1, 1)
        self._language.set_hexpand(True)
        grid.attach(self._language, 1, row, 1, 1)
        row += 1

        # Voice + preview
        voice_box = Gtk.Box(spacing=6)
        self._voice = Gtk.ComboBoxText()
        self._fill_voices()
        self._voice.connect("changed", self._on_voice_changed)
        voice = self._voice
        voice.set_hexpand(True)
        preview = Gtk.Button(label="Preview")
        preview.connect("clicked", lambda *_: self.app.speech.speak(PREVIEW_TEXT, mode=SpeakMode.VERBATIM))
        voice_box.pack_start(voice, True, True, 0)
        voice_box.pack_start(preview, False, False, 0)
        self._row(grid, row, "Voice", voice_box)
        row += 1

        # Speed
        speed = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0.25, 4.0, 0.05)
        speed.set_value(current.speed)
        speed.set_digits(2)
        speed.set_value_pos(Gtk.PositionType.RIGHT)
        for mark in (0.5, 1.0, 1.5, 2.0, 3.0):
            speed.add_mark(mark, Gtk.PositionType.BOTTOM, f"{mark:g}x")
        speed.connect("value-changed", lambda s: self._set("speed", round(s.get_value(), 2)))
        self._row(grid, row, "Speed", speed)
        row += 1

        # Output device
        device = Gtk.ComboBoxText()
        device.append("", "System default")
        for item in list_output_devices():
            device.append(item.uid, item.name)
        if device.set_active_id(current.audio_device) is False:
            device.append(current.audio_device, f"{current.audio_device} (not connected)")
            device.set_active_id(current.audio_device)
        device.connect("changed", lambda c: self._set("audio_device", c.get_active_id() or ""))
        self._row(grid, row, "Output device", device)
        row += 1

        grid.attach(Gtk.Separator(), 0, row, 2, 1)
        row += 1

        # Lemonade
        url_box = Gtk.Box(spacing=6)
        url = Gtk.Entry(text=current.base_url)
        url.set_hexpand(True)
        url.connect("focus-out-event", lambda e, *_: self._set("base_url", e.get_text()) or False)
        url.connect("activate", lambda e: self._set("base_url", e.get_text()))
        self._health = Gtk.Label(label="●")
        self._health.set_tooltip_text("Lemonade connection")
        url_box.pack_start(url, True, True, 0)
        url_box.pack_start(self._health, False, False, 4)
        self._row(grid, row, "Lemonade address", url_box)
        row += 1

        chat = Gtk.Entry(text=current.chat_model)
        chat.set_tooltip_text("Chat model for Summary and Explain. Pulled on first use if missing.")
        chat.connect("focus-out-event", lambda e, *_: self._set("chat_model", e.get_text().strip()) or False)
        chat.connect("activate", lambda e: self._set("chat_model", e.get_text().strip()))
        self._row(grid, row, "Chat model", chat)
        row += 1

        sentences = Gtk.SpinButton.new_with_range(1, 8, 1)
        sentences.set_value(current.clamped_explain_sentences)
        sentences.connect("value-changed", lambda s: self._set("explain_sentence_count", int(s.get_value())))
        self._row(grid, row, "Explain length (sentences)", sentences)
        row += 1

        audience = Gtk.ComboBoxText()
        for item in ExplainAudience:
            audience.append(item.value, item.title)
        audience.set_active_id(current.explain_audience.value)
        audience.connect("changed", lambda c: self._set("explain_audience", ExplainAudience(c.get_active_id())))
        self._row(grid, row, "Explain audience", audience)
        row += 1

        grid.attach(Gtk.Separator(), 0, row, 2, 1)
        row += 1

        for attr, label in (
            ("show_status_notifications", "Show status notifications"),
            ("play_sound_on_stop", "Play a sound when speech is stopped"),
            ("launch_at_login", "Launch at login"),
        ):
            check = Gtk.CheckButton(label=label)
            check.set_active(getattr(current, attr))
            check.connect("toggled", lambda c, a=attr: self._set(a, c.get_active()))
            grid.attach(check, 1, row, 1, 1)
            row += 1

        footer = Gtk.Box(spacing=6)
        footer.set_margin_top(8)
        self._status = Gtk.Label(label="Ready", xalign=0)
        self._status.get_style_context().add_class("dim-label")
        footer.pack_start(self._status, True, True, 0)
        close = Gtk.Button(label="Close")
        close.connect("clicked", lambda *_: self.destroy())
        footer.pack_end(close, False, False, 0)
        grid.attach(footer, 0, row, 2, 1)

        self.add(grid)
        self.show_all()
        self._refresh_health()
        self._health_source = GLib.timeout_add_seconds(5, self._refresh_health)
        self.connect("destroy", lambda *_: GLib.source_remove(self._health_source))

    def set_status(self, text: str) -> None:
        """Mirror the phase or notice here: GNOME hides banners from the focused app, and that may be this window."""
        self._status.set_text(text)

    def _set(self, attr: str, value) -> None:
        if getattr(self.store.current, attr) == value:
            return
        self.store.update(lambda s: setattr(s, attr, value))
        applog.write(f"setting {attr} changed")
        self.app.apply_settings()

    # -- engine / voices ---------------------------------------------------------
    def _fill_voices(self) -> None:
        current = self.store.current
        self._filling_voices = True
        try:
            self._voice.remove_all()
            if current.uses_phonon:
                choices = phonon_voices_for(current.phonon_language)
                selected = current.phonon_voice
            else:
                choices = list(VOICES)
                selected = current.voice
            if selected not in {c[0] for c in choices}:
                self._voice.append(selected, selected)
            for voice_id, name in choices:
                self._voice.append(voice_id, name)
            self._voice.set_active_id(selected)
        finally:
            self._filling_voices = False
        self._refresh_engine_hint()

    def _on_voice_changed(self, combo) -> None:
        if getattr(self, "_filling_voices", False):
            return
        voice_id = combo.get_active_id()
        if not voice_id:
            return
        self._set("phonon_voice" if self.store.current.uses_phonon else "voice", voice_id)

    def _on_engine_changed(self, combo) -> None:
        value = combo.get_active_id()
        if not value:
            return
        self._set("engine", Engine(value))
        self._fill_voices()

    def _on_language_changed(self, combo) -> None:
        code = combo.get_active_id()
        if not code:
            return
        self._set("phonon_language", code)
        if self.store.current.uses_phonon:
            self._fill_voices()

    def _refresh_engine_hint(self) -> None:
        if not hasattr(self, "_engine_hint"):
            return
        uses_phonon = self.store.current.uses_phonon
        for widget in (self._language, self._language_label):
            widget.set_visible(uses_phonon)
            widget.set_no_show_all(not uses_phonon)
        if not uses_phonon:
            self._engine_hint.set_text("Kokoro runs inside Lemonade; about a second to first audio.")
        elif phonon.is_installed():
            self._engine_hint.set_text(
                "Gradium's Phonon runs inside the app on the CPU: first audio in well under 0.1 s, about 10x realtime. "
                "Catalogue voices are downloaded the first time you pick them."
            )
        else:
            self._engine_hint.set_markup(
                "Phonon is not installed yet. Run <tt>linux/install-phonon.sh</tt> from the checkout (no root needed)."
            )

    def _refresh_health(self) -> bool:
        base_url = self.store.current.base_url

        def probe():
            healthy = self._client.is_healthy(base_url)
            GLib.idle_add(self._show_health, healthy)

        threading.Thread(target=probe, daemon=True).start()
        return True

    def _show_health(self, healthy: bool) -> bool:
        color = "#2ecc71" if healthy else "#e74c3c"
        self._health.set_markup(f'<span foreground="{color}" size="large">●</span>')
        self._health.set_tooltip_text("Lemonade is running" if healthy else "Lemonade is not running (it starts on first use)")
        return False
