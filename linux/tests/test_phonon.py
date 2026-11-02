import math
import struct
import threading
import unittest

from dynamite_tts import phonon


class DurationDeltaTests(unittest.TestCase):
    def test_normal_speed_is_zero_and_faster_is_negative(self):
        self.assertAlmostEqual(phonon.duration_delta_for_speed(1.0), 0.0)
        self.assertLess(phonon.duration_delta_for_speed(1.3), 0.0)
        self.assertGreater(phonon.duration_delta_for_speed(0.8), 0.0)

    def test_monotonic_and_clamped(self):
        deltas = [phonon.duration_delta_for_speed(s) for s in (0.25, 0.5, 0.8, 1.0, 1.5, 2.0, 4.0)]
        self.assertEqual(deltas, sorted(deltas, reverse=True))
        self.assertEqual(deltas[0], phonon.MAX_DURATION_DELTA)
        self.assertEqual(deltas[-1], -phonon.MAX_DURATION_DELTA)

    def test_garbage_gives_default_rate(self):
        for bad in (0, -1, float("nan"), "fast", None):
            self.assertEqual(phonon.duration_delta_for_speed(bad), 0.0)


class PcmTests(unittest.TestCase):
    def test_float_samples_become_clipped_int16(self):
        data = phonon.pcm16_bytes([0.0, 0.5, -0.5, 1.5, -1.5])
        values = struct.unpack("<5h", data)
        self.assertEqual(values[0], 0)
        self.assertAlmostEqual(values[1], 16383, delta=1)
        self.assertAlmostEqual(values[2], -16383, delta=1)
        self.assertEqual(values[3], 32767)
        self.assertEqual(values[4], -32767)

    def test_empty(self):
        self.assertEqual(phonon.pcm16_bytes([]), b"")


class FakeAudio:
    def __init__(self, chunks):
        self._chunks = list(chunks)
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.closed = True
        return False

    def __iter__(self):
        return self

    def __next__(self):
        if not self._chunks:
            raise StopIteration
        return self._chunks.pop(0)


class FakeTts:
    def __init__(self):
        self.calls = []
        self.conditions = None
        self.audio = None

    def stream(self, text, **kwargs):
        self.calls.append((text, kwargs))
        if kwargs.get("voice") == "Broken":
            raise ValueError("no such voice")
        self.audio = FakeAudio([[0.1] * 480, [0.2] * 480, [0.3] * 480])
        return self.audio

    def set_conditions(self, conditions):
        self.conditions = conditions


class FakeEngine:
    def __init__(self):
        self.tts = FakeTts()
        self.loaded = []

    def load(self, model_dir, lang):
        self.loaded.append((model_dir, lang))
        return self.tts

    def set_speed(self, speed):
        self.tts.set_conditions({"duration_delta": phonon.duration_delta_for_speed(speed)})


class PhononStreamTests(unittest.TestCase):
    def run_stream(self, voice="default", cancel_after=None):
        engine = FakeEngine()
        received = []
        done = threading.Event()
        result = {}

        stream = None

        def on_pcm(data):
            received.append(data)
            if cancel_after is not None and len(received) == cancel_after:
                stream.cancel()

        def on_complete(error):
            result["error"] = error
            done.set()

        stream = phonon.PhononStream(on_pcm, on_complete, engine=engine)
        stream.start("Hello there.", voice, 1.25, "en", "")
        self.assertTrue(done.wait(5))
        return engine, received, result

    def test_streams_pcm_and_passes_voice_and_speed(self):
        engine, received, result = self.run_stream(voice="Easton")
        self.assertIsNone(result["error"])
        self.assertEqual(len(received), 3)
        self.assertEqual(len(received[0]), 480 * 2)
        self.assertEqual(engine.loaded, [("", "en")])
        self.assertEqual(engine.tts.calls, [("Hello there.", {"voice": "Easton"})])
        self.assertLess(engine.tts.conditions["duration_delta"], 0)
        self.assertTrue(engine.tts.audio.closed)

    def test_default_voice_is_not_passed(self):
        engine, _, _ = self.run_stream(voice="default")
        self.assertEqual(engine.tts.calls, [("Hello there.", {})])

    def test_unknown_voice_falls_back_to_default(self):
        engine, received, result = self.run_stream(voice="Broken")
        self.assertIsNone(result["error"])
        self.assertEqual(len(received), 3)
        self.assertEqual([c[1] for c in engine.tts.calls], [{"voice": "Broken"}, {}])

    def test_cancel_stops_early_and_still_completes(self):
        engine, received, result = self.run_stream(cancel_after=1)
        self.assertEqual(len(received), 1)
        self.assertIsNone(result["error"])
        self.assertTrue(engine.tts.audio.closed)

    def test_missing_package_is_reported(self):
        class MissingEngine:
            def load(self, model_dir, lang):
                raise phonon.PhononError("Phonon is not installed.")

        done = threading.Event()
        result = {}
        stream = phonon.PhononStream(lambda d: None, lambda e: (result.__setitem__("error", e), done.set()), engine=MissingEngine())
        stream.start("Hi", "default", 1.0, "en", "")
        self.assertTrue(done.wait(5))
        self.assertIsInstance(result["error"], phonon.PhononError)


class HelperTests(unittest.TestCase):
    def test_threads_are_few(self):
        self.assertTrue(2 <= phonon.default_threads() <= 4)

    def test_quant(self):
        self.assertEqual(phonon.quant_for(None), "q8")
        self.assertEqual(phonon.quant_for(""), "q8")
        self.assertIsNone(phonon.quant_for("/nonexistent/folder"))


if __name__ == "__main__":
    unittest.main()
