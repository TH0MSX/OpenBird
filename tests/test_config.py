"""Runs anywhere (no Windows libraries needed):  python -m unittest discover tests"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.environ["OPENBIRD_HOME"] = tempfile.mkdtemp(prefix="openbird_test_")

from openbird import config  # noqa: E402


class SettingsTests(unittest.TestCase):
    def _load(self, values):
        path = os.path.join(tempfile.mkdtemp(), "config.json")
        config.save_settings(values, path)
        return config.load_settings(path)

    def test_defaults_are_local_only_and_under_app_data(self):
        s = self._load({})
        self.assertFalse(s["cloud_ai_enabled"])
        self.assertTrue(s["db_path"].startswith(os.environ["OPENBIRD_HOME"]))

    def test_keys_ignored_while_cloud_ai_is_off(self):
        s = self._load({"gemini_api_key": "abc", "groq_api_key": "def"})
        self.assertEqual(s["gemini_api_key"], "")
        self.assertEqual(s["groq_api_key"], "")

    def test_keys_used_when_cloud_ai_is_on(self):
        s = self._load({"cloud_ai_enabled": True, "groq_api_key": "def"})
        self.assertEqual(s["groq_api_key"], "def")

    def test_empty_path_falls_back_to_default(self):
        s = self._load({"db_path": ""})
        self.assertTrue(s["db_path"].endswith("openbird.db"))

    def test_no_personal_paths_in_source(self):
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        for folder, _, files in os.walk(root):
            if any(part in folder for part in (".venv", ".git", "tests", "dist", "legacy")):
                continue
            for name in files:
                if name.endswith((".py", ".ps1", ".bat")):
                    with open(os.path.join(folder, name), encoding="utf-8", errors="ignore") as f:
                        self.assertNotIn("C:\\Users\\", f.read(), f"hard-coded user path in {name}")


class PrivacyTests(unittest.TestCase):
    def test_password_managers_and_private_windows_are_excluded(self):
        self.assertTrue(config.is_capture_excluded("Vault - 1Password", "1Password.exe"))
        self.assertTrue(config.is_capture_excluded("anything", "KeePassXC.exe"))
        self.assertTrue(config.is_capture_excluded("New tab - [InPrivate] - Microsoft Edge", "msedge.exe"))
        self.assertTrue(config.is_capture_excluded("Reset your password - Contoso", "chrome.exe"))

    def test_ordinary_windows_are_captured(self):
        self.assertFalse(config.is_capture_excluded("Q3 audit.xlsx - Excel", "EXCEL.EXE"))
        self.assertFalse(config.is_capture_excluded("", ""))

    def test_custom_patterns(self):
        self.assertTrue(config.is_capture_excluded("Payroll - HR Portal", "msedge.exe", ["payroll"]))


class HotkeyTests(unittest.TestCase):
    def test_parse(self):
        self.assertEqual(config.parse_hotkey("win+alt+r"), (0x0008 | 0x0001, 0x52))
        self.assertEqual(config.parse_hotkey("Ctrl+Shift+F9"), (0x0002 | 0x0004, 0x78))

    def test_invalid(self):
        for bad in ("r", "win+alt", "ctrl+a+b", "ctrl+foo", ""):
            with self.assertRaises(ValueError):
                config.parse_hotkey(bad)


if __name__ == "__main__":
    unittest.main()
