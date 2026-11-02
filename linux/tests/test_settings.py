import json
import tempfile
import unittest
from pathlib import Path

from dynamite_tts.settings import AppSettings, ExplainAudience, SettingsStore, SpeakMode


class SettingsTests(unittest.TestCase):
    def test_mode_cycle(self):
        self.assertEqual(SpeakMode.VERBATIM.next(), SpeakMode.SUMMARY)
        self.assertEqual(SpeakMode.SUMMARY.next(), SpeakMode.EXPLAIN)
        self.assertEqual(SpeakMode.EXPLAIN.next(), SpeakMode.VERBATIM)

    def test_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "settings.json"
            store = SettingsStore(path)
            store.update(lambda s: (setattr(s, "speed", 1.5), setattr(s, "speak_mode", SpeakMode.EXPLAIN),
                                    setattr(s, "explain_audience", ExplainAudience.GENERAL)))
            data = json.loads(path.read_text())
            self.assertEqual(data["speak_mode"], "explain")
            self.assertEqual(data["explain_audience"], "general")
            again = SettingsStore(path).current
            self.assertEqual(again.speed, 1.5)
            self.assertEqual(again.speak_mode, SpeakMode.EXPLAIN)
            self.assertEqual(again.explain_audience, ExplainAudience.GENERAL)

    def test_clamps_and_ignores_junk(self):
        settings = AppSettings.from_json({"speed": 99, "explain_sentence_count": 50, "speak_mode": "bogus",
                                          "unknown": 1, "base_url": " http://localhost:1/ ", "speak_hotkey": ""})
        self.assertEqual(settings.speed, 4.0)
        self.assertEqual(settings.explain_sentence_count, 8)
        self.assertEqual(settings.speak_mode, SpeakMode.VERBATIM)
        self.assertEqual(settings.base_url, "http://localhost:1")
        self.assertEqual(settings.speak_hotkey, "<Control><Shift>s")

    def test_missing_file_gives_defaults(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(SettingsStore(Path(tmp) / "none.json").current, AppSettings())


if __name__ == "__main__":
    unittest.main()
