import json
import tempfile
import unittest
from pathlib import Path

from dynamite_tts.settings import AppSettings, Engine, ExplainAudience, SettingsStore, SpeakMode, phonon_voices_for


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

    def test_engine_fields_round_trip_and_default_to_phonon(self):
        self.assertEqual(AppSettings().engine, Engine.PHONON)
        self.assertTrue(AppSettings().uses_phonon)
        self.assertEqual(AppSettings.from_json({"engine": "kokoro"}).engine, Engine.KOKORO)
        settings = AppSettings.from_json({"engine": "phonon", "phonon_voice": " Easton ", "phonon_language": "EN", "phonon_model_dir": " "})
        self.assertTrue(settings.uses_phonon)
        self.assertEqual(settings.phonon_voice, "Easton")
        self.assertEqual(settings.phonon_language, "en")
        self.assertEqual(settings.phonon_model_dir, "")
        self.assertEqual(settings.to_json()["engine"], "phonon")
        self.assertEqual(AppSettings.from_json({"engine": "bogus"}).engine, Engine.PHONON)

    def test_phonon_voices_list_default_first_then_language(self):
        voices = phonon_voices_for("en")
        self.assertEqual(voices[0][0], "default")
        names = [v[0] for v in voices]
        self.assertIn("Easton", names)
        self.assertLess(names.index("Easton"), names.index("Léa") if "Léa" in names else len(names))
        self.assertEqual(len(names), len(set(names)))

    def test_hotkey_stops_speech_defaults_on_and_persists(self):
        self.assertTrue(AppSettings.from_json({"voice": "af_sky"}).hotkey_stops_speech)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "settings.json"
            SettingsStore(path).update(lambda s: setattr(s, "hotkey_stops_speech", False))
            self.assertFalse(SettingsStore(path).current.hotkey_stops_speech)

    def test_missing_file_gives_defaults(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(SettingsStore(Path(tmp) / "none.json").current, AppSettings())


if __name__ == "__main__":
    unittest.main()
