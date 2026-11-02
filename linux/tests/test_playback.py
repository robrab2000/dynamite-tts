import os
import shutil
import tempfile
import threading
import unittest
from pathlib import Path

from dynamite_tts import playback


class PCMPlayerTests(unittest.TestCase):
    """Uses a fake player (cat to a file) so the suite runs on CI without audio hardware."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.out = Path(self.tmp) / "out.pcm"
        fake = Path(self.tmp) / "pw-play"
        fake.write_text(f"#!/bin/sh\nexec cat > '{self.out}'\n")
        fake.chmod(0o755)
        self._path = os.environ["PATH"]
        os.environ["PATH"] = f"{self.tmp}:{self._path}"

    def tearDown(self):
        os.environ["PATH"] = self._path
        shutil.rmtree(self.tmp)

    def test_streams_and_reports_drain(self):
        player = playback.PCMPlayer()
        drained = threading.Event()
        player.on_drained = drained.set
        player.prepare("")
        for i in range(10):
            player.enqueue(bytes([i % 256]) * 9600)
        player.finish()
        self.assertTrue(drained.wait(5))
        self.assertEqual(self.out.stat().st_size, 96000)
        self.assertFalse(player.is_active)

    def test_stop_discards_and_blocks_late_drain(self):
        player = playback.PCMPlayer()
        drained = threading.Event()
        player.on_drained = drained.set
        player.prepare("")
        player.enqueue(b"\0" * 4800)
        player.stop(play_chime=False)
        player.finish()
        self.assertFalse(drained.wait(0.5))
        self.assertFalse(player.is_active)

    def test_level_metering(self):
        player = playback.PCMPlayer()
        player.prepare("")
        loud = b"\xff\x7f" * 480  # one 20 ms frame at full scale
        player.enqueue(loud)
        self.assertEqual(len(player._levels), 1)
        self.assertEqual(player._levels[0], 1.0)
        player.stop(play_chime=False)

    def test_command_selection(self):
        self.assertEqual(playback._player_command("")[0], "pw-play")
        self.assertIn("--target", playback._player_command("some.sink"))


if __name__ == "__main__":
    unittest.main()
