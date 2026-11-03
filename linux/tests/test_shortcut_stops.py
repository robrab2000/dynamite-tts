"""The speak shortcut stops current speech when that option is on."""
import sys
import tempfile
import types
import unittest
from pathlib import Path


def _install_stubs() -> None:
    try:
        import gi
        gi.require_version("Gtk", "3.0")
        from gi.repository import GLib, Gtk  # noqa: F401
        return
    except Exception:
        pass

    gi = types.ModuleType("gi")
    gi.require_version = lambda *args, **kwargs: None
    repository = types.ModuleType("gi.repository")

    class GLib:
        PRIORITY_HIGH = 0

        @staticmethod
        def idle_add(*args, **kwargs):
            return 0

        @staticmethod
        def unix_signal_add(*args, **kwargs):
            return 0

        @staticmethod
        def set_prgname(*args, **kwargs):
            return None

        @staticmethod
        def set_application_name(*args, **kwargs):
            return None

    class _Widget:
        def __init__(self, *args, **kwargs):
            pass

        def connect(self, *args, **kwargs):
            return None

        def append(self, *args, **kwargs):
            return None

        def show_all(self):
            return None

    class Gtk:
        Menu = _Widget
        MenuItem = _Widget
        SeparatorMenuItem = _Widget

    repository.GLib = GLib
    repository.Gtk = Gtk
    gi.repository = repository
    sys.modules["gi"] = gi
    sys.modules["gi.repository"] = repository
    try:
        import fcntl  # noqa: F401
    except ModuleNotFoundError:
        fcntl = types.ModuleType("fcntl")
        fcntl.flock = lambda *args, **kwargs: None
        fcntl.LOCK_EX = 2
        fcntl.LOCK_NB = 4
        sys.modules["fcntl"] = fcntl


_install_stubs()

from dynamite_tts.app import DynamiteApp  # noqa: E402
from dynamite_tts.settings import SettingsStore  # noqa: E402
from dynamite_tts.speech import SpeechSession  # noqa: E402
import dynamite_tts.app as appmod  # noqa: E402


class _Speech:
    def __init__(self, busy: bool):
        self.is_busy = busy
        self.stopped = False
        self.spoken = []

    def stop(self, play_chime: bool) -> None:
        self.stopped = True
        self.play_chime = play_chime
        self.is_busy = False

    def speak(self, text: str) -> None:
        self.spoken.append(text)


class ShortcutStopsTests(unittest.TestCase):
    def _app(self, stops: bool, busy: bool):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        store = SettingsStore(Path(directory.name) / "settings.json")
        store.update(lambda settings: setattr(settings, "hotkey_stops_speech", stops))
        app = DynamiteApp.__new__(DynamiteApp)
        app.store = store
        app.speech = _Speech(busy)
        app.notices = []
        app.show_notice = app.notices.append
        return app

    def test_busy_shortcut_stops_without_reading_a_new_selection(self):
        app = self._app(stops=True, busy=True)
        captured = []
        appmod.selection.capture = lambda: captured.append("new") or "new"
        app.speak_selection()
        self.assertTrue(app.speech.stopped)
        self.assertTrue(app.speech.play_chime)
        self.assertEqual(app.speech.spoken, [])
        self.assertEqual(captured, [])

    def test_busy_shortcut_reads_the_new_selection_when_the_option_is_off(self):
        app = self._app(stops=False, busy=True)
        appmod.selection.capture = lambda: "a different passage"
        app.speak_selection()
        self.assertFalse(app.speech.stopped)
        self.assertEqual(app.speech.spoken, ["a different passage"])

    def test_idle_shortcut_reads_the_selection(self):
        app = self._app(stops=True, busy=False)
        appmod.selection.capture = lambda: "hello there"
        app.speak_selection()
        self.assertFalse(app.speech.stopped)
        self.assertEqual(app.speech.spoken, ["hello there"])

    def test_session_is_busy_as_soon_as_speech_starts(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        session = SpeechSession(SettingsStore(Path(directory.name) / "settings.json"))
        session._run_guarded = lambda *args, **kwargs: None
        self.assertFalse(session.is_busy)
        session.speak("Hello from the shortcut.")
        self.assertTrue(session.is_busy)
        session.stop(play_chime=True)
        self.assertFalse(session.is_busy)


if __name__ == "__main__":
    unittest.main()
