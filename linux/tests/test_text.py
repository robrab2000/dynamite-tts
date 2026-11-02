import unittest

from dynamite_tts import text
from dynamite_tts.settings import ExplainAudience


class SanitizeTests(unittest.TestCase):
    def test_markdown_links_keep_label(self):
        self.assertEqual(text.sanitize("See [the docs](https://example.com/x) now"), "See the docs now")

    def test_raw_urls_become_host(self):
        self.assertEqual(text.sanitize("Go to https://www.example.com/path?q=1 today"), "Go to link to example.com today")

    def test_code_headers_bullets_emphasis(self):
        raw = "# Title\n\n- **bold** item\n* _soft_ item\n```python\nprint(1)\n```\nuse `x`"
        self.assertEqual(text.sanitize(raw), "Title\nbold item\nsoft item\nprint(1)\nuse x")

    def test_whitespace_collapses(self):
        self.assertEqual(text.sanitize("a \t b\r\n\r\n\r\n\r\nc"), "a b\n\nc")

    def test_empty(self):
        self.assertEqual(text.sanitize("  \n "), "")


class PromptTests(unittest.TestCase):
    def test_explain_clamps_sentences(self):
        self.assertIn("about 8 sentences", text.explain_prompt(ExplainAudience.GENERAL, 40))
        self.assertIn("about 1 sentences", text.explain_prompt(ExplainAudience.TECHNICAL, 0))

    def test_explain_user_message_without_context(self):
        self.assertEqual(text.explain_user_message(" Electron ", None), "Electron")
        self.assertEqual(text.explain_user_message("Electron", "electron"), "Electron")
        self.assertEqual(text.explain_user_message("Electron", "Electron UI"), "Electron")

    def test_explain_user_message_with_context(self):
        message = text.explain_user_message("Electron", "Browsers and Electron UIs render HTML.")
        self.assertTrue(message.startswith("Surrounding text:\nBrowsers and Electron UIs render HTML."))
        self.assertTrue(message.endswith("\nElectron"))

    def test_explain_user_message_truncates_long_context(self):
        message = text.explain_user_message("x", "y" * 2000)
        self.assertIn("y" * 1500 + "…", message)
        self.assertNotIn("y" * 1501, message)


if __name__ == "__main__":
    unittest.main()
