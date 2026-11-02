import unittest

from dynamite_tts.hotkeys import display_name


class DisplayNameTests(unittest.TestCase):
    def test_default(self):
        self.assertEqual(display_name("<Control><Shift>s"), "Ctrl+Shift+S")

    def test_primary_alias_and_named_key(self):
        self.assertEqual(display_name("<Primary><Alt>space"), "Ctrl+Alt+Space")

    def test_super(self):
        self.assertEqual(display_name("<Super>F9"), "Super+F9")


if __name__ == "__main__":
    unittest.main()
