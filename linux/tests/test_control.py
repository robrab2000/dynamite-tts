import os
import tempfile
import unittest

from dynamite_tts import control


class SendCommandTests(unittest.TestCase):
    def test_returns_none_when_not_running(self):
        with tempfile.TemporaryDirectory() as tmp:
            old = os.environ.get("XDG_RUNTIME_DIR")
            os.environ["XDG_RUNTIME_DIR"] = tmp
            try:
                self.assertIsNone(control.send_command("ping"))
            finally:
                if old is None:
                    del os.environ["XDG_RUNTIME_DIR"]
                else:
                    os.environ["XDG_RUNTIME_DIR"] = old


if __name__ == "__main__":
    unittest.main()
