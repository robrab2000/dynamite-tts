"""Status notifications.

Wayland compositors do not let a background app place a floating pill at the top of the
screen without taking focus, so the phase ("Summarizing…", "Speaking") is shown as a
desktop notification that is updated in place and closed when speech ends. GNOME draws it
top-centre, exactly where the Windows and macOS bubbles sit. It carries a Stop action.
"""
from __future__ import annotations

from gi.repository import GLib

from . import APP_NAME, applog

try:
    import gi

    gi.require_version("Notify", "0.7")
    from gi.repository import Notify

    _HAVE_NOTIFY = True
except (ImportError, ValueError):
    Notify = None
    _HAVE_NOTIFY = False


class StatusNotifier:
    def __init__(self, on_stop, icon_name: str = "audio-speakers-symbolic"):
        self._on_stop = on_stop
        self._icon = icon_name
        self._notification = None
        self._hide_source = None
        self._enabled = False
        if _HAVE_NOTIFY and Notify.init(APP_NAME):
            self._enabled = True
        else:
            applog.write("notifications unavailable; phases are logged only")

    def show(self, text: str, persistent: bool) -> None:
        if not self._enabled:
            return
        self._cancel_hide()
        if self._notification is None:
            self._notification = Notify.Notification.new(APP_NAME, text, self._icon)
            self._notification.set_hint("desktop-entry", GLib.Variant("s", "dynamite-tts"))
            self._notification.set_hint("transient", GLib.Variant("b", True))
            self._notification.connect("closed", self._closed)
        else:
            self._notification.update(APP_NAME, text, self._icon)
        self._notification.clear_actions()
        if persistent:
            self._notification.add_action("stop", "Stop", lambda *_: self._on_stop())
            self._notification.set_hint("resident", GLib.Variant("b", True))
        else:
            self._notification.set_hint("resident", GLib.Variant("b", False))
        try:
            self._notification.show()
        except GLib.Error as error:
            applog.write(f"notification failed: {error}")

    def show_notice(self, text: str) -> None:
        self.show(text, persistent=False)
        self._hide_source = GLib.timeout_add(2500, self._hide_timeout)

    def hide(self) -> None:
        self._cancel_hide()
        if self._notification is None:
            return
        notification, self._notification = self._notification, None
        try:
            notification.close()
        except GLib.Error:
            pass

    def _hide_timeout(self) -> bool:
        self._hide_source = None
        self.hide()
        return False

    def _cancel_hide(self) -> None:
        if self._hide_source is not None:
            GLib.source_remove(self._hide_source)
            self._hide_source = None

    def _closed(self, notification) -> None:
        if notification is self._notification:
            self._notification = None
