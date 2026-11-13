"""
OpenBird - Settings and data locations.

One place decides where things live. Nothing else in the code base may contain
an absolute path. Order of precedence for the settings file:

1. an explicit path passed by the caller
2. the OPENBIRD_CONFIG environment variable
3. %LOCALAPPDATA%\\OpenBird\\config.json (created on first run)

On first run, a legacy ``config.json`` sitting next to ``main.py`` (OpenBird
v2.x) is imported once, so existing recordings, notes and the Whisper model
keep working without edits.
"""

import os
import sys
import json
import logging
from typing import Dict, List, Optional

logger = logging.getLogger("openbird.config")

APP_NAME = "OpenBird"
APP_VERSION = "2.5.0"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

WHISPER_MODEL_NAME = "sherpa-onnx-whisper-base.en"

DEFAULT_CAPTURE_EXCLUSIONS: List[str] = [
    # Password managers (matched against window title and process name)
    "1password", "bitwarden", "keepass", "lastpass", "dashlane", "nordpass",
    "proton pass", "enpass", "roboform", "credential manager",
    # Private browsing (Edge and Firefox put this in the window title)
    "inprivate", "private browsing", "incognito",
    # Sign-in and security surfaces
    "password", "windows security", "sign in to your account",
    # Banking and payments
    "bank", "paypal", "revolut", "wise.com",
]

_settings: Dict = {}


def app_data_dir() -> str:
    """Per-user, non-roaming data root: %LOCALAPPDATA%\\OpenBird."""
    override = os.environ.get("OPENBIRD_HOME")
    if override:
        return override
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        base = os.path.join(os.path.expanduser("~"), ".local", "share")
    return os.path.join(base, APP_NAME)


def documents_dir() -> str:
    """The user's real Documents folder (follows OneDrive redirection on Windows)."""
    if sys.platform == "win32":
        try:
            import ctypes
            from ctypes import wintypes
            buf = ctypes.create_unicode_buffer(wintypes.MAX_PATH)
            # CSIDL_PERSONAL = 5, SHGFP_TYPE_CURRENT = 0
            if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buf) == 0 and buf.value:
                return buf.value
        except Exception as e:
            logger.warning(f"Could not resolve the Documents folder, using the home folder: {e}")
    return os.path.join(os.path.expanduser("~"), "Documents")


def _detect_user_name() -> str:
    """First name of the signed-in user, used as the host speaker label."""
    if sys.platform == "win32":
        try:
            import ctypes
            size = ctypes.c_ulong(256)
            buf = ctypes.create_unicode_buffer(size.value)
            # NameDisplay = 3
            if ctypes.windll.secur32.GetUserNameExW(3, buf, ctypes.byref(size)) and buf.value.strip():
                return buf.value.strip().split()[0]
        except Exception:
            pass
    try:
        import getpass
        name = getpass.getuser()
        return name[:1].upper() + name[1:] if name else "Me"
    except Exception:
        return "Me"


def default_settings() -> Dict:
    data = os.path.join(app_data_dir(), "data")
    return {
        "app_name": APP_NAME,
        "version": APP_VERSION,
        "user_name": "",
        "db_path": os.path.join(data, "openbird.db"),
        "storage_dir": os.path.join(documents_dir(), APP_NAME),
        "recordings_dir": os.path.join(data, "recordings"),
        "screenshots_dir": os.path.join(data, "screenshots"),
        "whisper_model_dir": os.path.join(app_data_dir(), "models", WHISPER_MODEL_NAME),
        "whisper_threads": 4,
        "sample_rate": 16000,
        "screen_ocr_enabled": True,
        "screen_ocr_interval_seconds": 20,
        "screenshot_retention_days": 7,
        "capture_all_monitors": True,
        "capture_exclusions": list(DEFAULT_CAPTURE_EXCLUSIONS),
        "auto_transcribe": True,
        "auto_transcribe_threshold": 0.015,
        "min_speech_seconds": 2.0,
        "silence_timeout_seconds": 25.0,
        "default_template": "executive",
        "hotkey": "win+alt+r",
        # Local only by default: nothing leaves this PC unless this is true
        # AND a key is present.
        "cloud_ai_enabled": False,
        "gemini_api_key": "",
        "groq_api_key": "",
        "gemini_model": "gemini-2.5-flash",
        "groq_model": "llama-3.3-70b-versatile",
    }


# Keys copied from an OpenBird v2.x config.json on first run.
_LEGACY_KEYS = [
    "db_path", "storage_dir", "recordings_dir", "screenshots_dir", "whisper_model_dir",
    "whisper_threads", "sample_rate", "screen_ocr_enabled", "screen_ocr_interval_seconds",
    "screenshot_retention_days", "auto_transcribe", "auto_transcribe_threshold",
    "min_speech_seconds", "silence_timeout_seconds", "default_template",
    "gemini_api_key", "groq_api_key",
]


def user_config_path() -> str:
    return os.environ.get("OPENBIRD_CONFIG") or os.path.join(app_data_dir(), "config.json")


def _read_json(path: str) -> Optional[Dict]:
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except FileNotFoundError:
        return None
    except Exception as e:
        logger.error(f"Settings file {path} is unreadable ({e}); using defaults.")
        return None


def save_settings(values: Dict, path: str = None) -> str:
    path = path or user_config_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(values, f, indent=2, ensure_ascii=False)
    os.replace(tmp, path)
    return path


def _import_legacy(legacy_path: str) -> Dict:
    legacy = _read_json(legacy_path) or {}
    imported = {k: legacy[k] for k in _LEGACY_KEYS if legacy.get(k) not in (None, "")}
    if imported:
        logger.info(f"Imported {len(imported)} settings from legacy {legacy_path}.")
    return imported


def load_settings(explicit_path: str = None) -> Dict:
    """Loads settings, creating the user settings file on first run."""
    global _settings
    defaults = default_settings()
    path = explicit_path or user_config_path()
    stored = _read_json(path)

    if stored is None and not explicit_path:
        stored = _import_legacy(os.path.join(REPO_ROOT, "config.json"))
        try:
            save_settings({**defaults, **stored}, path)
            logger.info(f"Created settings file at {path}.")
        except Exception as e:
            logger.error(f"Could not write settings file {path}: {e}")
    stored = stored or {}

    merged = dict(defaults)
    for key, value in stored.items():
        # An empty path or name falls back to the default instead of breaking startup.
        if value in (None, "") and isinstance(defaults.get(key), str) and defaults.get(key):
            continue
        merged[key] = value

    merged["app_name"] = APP_NAME
    merged["version"] = APP_VERSION
    if not merged.get("user_name"):
        merged["user_name"] = _detect_user_name()
    if not isinstance(merged.get("capture_exclusions"), list):
        merged["capture_exclusions"] = list(DEFAULT_CAPTURE_EXCLUSIONS)

    # Keys may also come from the environment, but only count when cloud AI is on.
    if merged.get("cloud_ai_enabled"):
        merged["gemini_api_key"] = merged.get("gemini_api_key") or os.environ.get("GEMINI_API_KEY", "")
        merged["groq_api_key"] = merged.get("groq_api_key") or os.environ.get("GROQ_API_KEY", "")
    else:
        merged["gemini_api_key"] = ""
        merged["groq_api_key"] = ""

    _settings = merged
    return merged


def settings() -> Dict:
    """The loaded settings (loads them on first use)."""
    return _settings or load_settings()


def user_name() -> str:
    return settings().get("user_name") or "Me"


def log_dir() -> str:
    return os.path.join(app_data_dir(), "logs")


def default_path(key: str) -> str:
    """Default location for a path setting, for modules constructed without one."""
    return settings().get(key) or default_settings()[key]


# ---------------------------------------------------------------- privacy

def is_capture_excluded(window_title: str, process_name: str = "", patterns: List[str] = None) -> bool:
    """True when the foreground window must never be captured, OCR'd or stored."""
    if patterns is None:
        patterns = settings().get("capture_exclusions", DEFAULT_CAPTURE_EXCLUSIONS)
    haystack = f"{window_title or ''}\n{process_name or ''}".lower()
    for p in patterns:
        p = (p or "").strip().lower()
        if p and p in haystack:
            return True
    return False


def foreground_window_info() -> Dict:
    """Raw title and executable name of the foreground window (Windows only)."""
    info = {"title": "", "process": ""}
    if sys.platform != "win32":
        return info
    try:
        import ctypes
        from ctypes import wintypes
        # Private DLL handles: setting restype/argtypes here must not affect other modules.
        user32 = ctypes.WinDLL("user32")
        kernel32 = ctypes.WinDLL("kernel32")
        user32.GetForegroundWindow.restype = wintypes.HWND
        user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
        user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        hwnd = user32.GetForegroundWindow()
        if not hwnd:
            return info
        length = user32.GetWindowTextLengthW(hwnd)
        if length:
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            info["title"] = buf.value

        pid = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value:
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            # PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
            handle = kernel32.OpenProcess(0x1000, False, pid.value)
            if handle:
                try:
                    size = wintypes.DWORD(1024)
                    path_buf = ctypes.create_unicode_buffer(size.value)
                    kernel32.QueryFullProcessImageNameW.argtypes = [
                        wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
                    if kernel32.QueryFullProcessImageNameW(handle, 0, path_buf, ctypes.byref(size)):
                        info["process"] = os.path.basename(path_buf.value)
                finally:
                    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
                    kernel32.CloseHandle(handle)
    except Exception as e:
        logger.warning(f"Could not inspect the foreground window: {e}")
    return info


def foreground_is_excluded() -> bool:
    info = foreground_window_info()
    return is_capture_excluded(info["title"], info["process"])


# ---------------------------------------------------------------- hotkey

_MODIFIERS = {"alt": 0x0001, "ctrl": 0x0002, "control": 0x0002, "shift": 0x0004, "win": 0x0008}
_NAMED_KEYS = {"space": 0x20, "pause": 0x13, "insert": 0x2D, "home": 0x24, "end": 0x23}


def parse_hotkey(spec: str):
    """'win+alt+r' -> (modifier_flags, virtual_key). Raises ValueError when invalid."""
    parts = [p.strip().lower() for p in (spec or "").split("+") if p.strip()]
    mods, vk = 0, None
    for p in parts:
        if p in _MODIFIERS:
            mods |= _MODIFIERS[p]
        elif vk is not None:
            raise ValueError(f"Hotkey '{spec}' names more than one key.")
        elif len(p) == 1 and (p.isalpha() or p.isdigit()):
            vk = ord(p.upper())
        elif p in _NAMED_KEYS:
            vk = _NAMED_KEYS[p]
        elif p.startswith("f") and p[1:].isdigit() and 1 <= int(p[1:]) <= 24:
            vk = 0x70 + int(p[1:]) - 1
        else:
            raise ValueError(f"Hotkey '{spec}' has an unknown key '{p}'.")
    if vk is None or mods == 0:
        raise ValueError(f"Hotkey '{spec}' needs at least one modifier and one key.")
    return mods, vk
