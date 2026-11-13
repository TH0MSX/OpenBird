"""
OpenBird - 100% Native Windows 11 Desktop AI Utility
Zero Chromium / Zero Webview / Sovereign Production Release
Visual Timeline Replay, Natural Language Memory Q&A, Executive Daily Digest, Channel Diarization, and Per-Monitor V2 High-DPI.
"""

import os
import sys
import time
import math
import ctypes
from ctypes import wintypes
import argparse
import logging
import threading
import subprocess
from typing import Optional, List, Dict

import customtkinter as ctk
import pywinstyles
from PIL import Image

# Fix 1: Windows 11 Per-Monitor V2 High-DPI Awareness for razor-sharp typography
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

# Ensure local imports work cleanly
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from openbird import config
from logging.handlers import RotatingFileHandler

# Configure logging: rolling file in %LOCALAPPDATA%\\OpenBird\\logs (never inside the repo)
os.makedirs(config.log_dir(), exist_ok=True)
log_file = os.path.join(config.log_dir(), "openbird.log")
_log_handlers = [RotatingFileHandler(log_file, maxBytes=2_000_000, backupCount=3, encoding="utf-8")]
if sys.stdout is not None:  # pythonw has no console
    _log_handlers.append(logging.StreamHandler(sys.stdout))
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    handlers=_log_handlers
)
logger = logging.getLogger("openbird.main")

# ---- Crash visibility. Under pythonw there is no console, so without this an
# uncaught error or a native crash ends the app with no trace at all.
import faulthandler
import platform
import traceback

_crash_path = os.path.join(config.log_dir(), "crash.log")
try:
    _crash_file = open(_crash_path, "a", encoding="utf-8")
    _crash_file.write(f"\n==== start {time.strftime('%Y-%m-%d %H:%M:%S')} ====\n")
    _crash_file.flush()
    faulthandler.enable(file=_crash_file, all_threads=True)  # access violations, aborts
except Exception as _e:
    _crash_file = None
    logger.warning(f"Crash log unavailable: {_e}")


def _log_uncaught(exc_type, exc, tb):
    logger.critical("Uncaught error:\n" + "".join(traceback.format_exception(exc_type, exc, tb)))


def _log_thread_uncaught(args):
    name = args.thread.name if args.thread else "?"
    logger.critical(f"Uncaught error in thread {name}:\n"
                    + "".join(traceback.format_exception(args.exc_type, args.exc_value, args.exc_traceback)))


sys.excepthook = _log_uncaught
threading.excepthook = _log_thread_uncaught


def _show_fatal(message: str):
    """A plain Windows message box, so a failed start is never silent."""
    try:
        ctypes.windll.user32.MessageBoxW(None, message, "OpenBird could not start", 0x10)
    except Exception:
        pass

from openbird.service import MeetingService
from openbird.dialogs import (
    CalendarDialog,
    TagEditDialog,
    ScreenSearchDialog,
    AudioDiagnosticsDialog,
    AskOpenBirdDialog,
    TimelineScrubDialog,
    HealthDialog
)

# Win32 Single-Instance Mutex with Robust Bring-To-Front
_app_mutex = None
def ensure_single_instance():
    global _app_mutex
    kernel32 = ctypes.windll.kernel32
    user32 = ctypes.windll.user32
    _app_mutex = kernel32.CreateMutexW(None, False, "Global\\OpenBird_Mutex")
    if kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        hwnd = user32.FindWindowW(None, "OpenBird")
        if hwnd:
            user32.ShowWindow(hwnd, 9)  # SW_RESTORE
            # Toggle HWND_TOPMOST then HWND_NOTOPMOST to force Windows 11 foreground activation
            user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002)
            user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0001 | 0x0002)
            user32.SetForegroundWindow(hwnd)
        sys.exit(0)

# Fluent 2 Color Palette (Dark Theme)
COLOR_BG = "#181818"
COLOR_CARD_BG = "#222222"
COLOR_CARD_HOVER = "#2a2a2a"
COLOR_BORDER = "#303030"
COLOR_ACCENT = "#0078d4"          # Fluent Blue
COLOR_ACCENT_HOVER = "#0084c7"
COLOR_RECORDING = "#e81123"       # Crimson Recording
COLOR_RECORDING_HOVER = "#c50f1f"
COLOR_TEXT_PRIMARY = "#ffffff"
COLOR_TEXT_SECONDARY = "#9e9e9e"
COLOR_TEXT_MUTED = "#6a6a6a"
COLOR_GREEN = "#107c41"
COLOR_AMBER = "#d83b01"

FONT_FAMILY = "Segoe UI Variable Display"
FONT_TEXT = "Segoe UI Variable Text"
FONT_MONO = "Cascadia Code"


class AudioMeter(ctk.CTkCanvas):
    """Native Win32 Canvas Audio Equalizer Meter."""
    def __init__(self, master, num_bars=14, width=220, height=18, **kwargs):
        super().__init__(master, width=width, height=height, bg=COLOR_CARD_BG, highlightthickness=0, **kwargs)
        self.num_bars = num_bars
        self.canvas_width = width
        self.canvas_height = height
        self.bar_width = (width - (num_bars - 1) * 3) / num_bars
        self.current_level = 0.0
        self.is_recording = False
        self.draw_bars(0.0)

    def draw_bars(self, level: float, is_recording: bool = False):
        self.delete("all")
        self.current_level = level
        self.is_recording = is_recording

        active_bars = min(self.num_bars, int(math.sqrt(max(0.0, level)) * 36))
        if is_recording and active_bars == 0:
            active_bars = 1

        for i in range(self.num_bars):
            x0 = i * (self.bar_width + 3)
            x1 = x0 + self.bar_width
            max_h = self.canvas_height - 3
            bar_h = max(3, int(max_h * ((i + 1) / self.num_bars)))
            y0 = self.canvas_height - bar_h
            y1 = self.canvas_height

            if i < active_bars:
                fill_color = COLOR_RECORDING if is_recording else "#60cdff"
            else:
                fill_color = "#333333"

            self.create_rectangle(x0, y0, x1, y1, fill=fill_color, outline="")


class OpenBirdNativeApp(ctk.CTk):
    def __init__(self, service: MeetingService):
        super().__init__()
        self.service = service
        self.selected_meeting_id: Optional[str] = None
        self.selected_tag_filter: Optional[str] = None
        self.all_meetings: List[Dict] = []
        self._is_closing = False
        self._search_debounce_job = None
        self.tray_icon = None

        # Personalization & System Accent
        theme_info = self.service.get_theme_info()
        self.accent_color = theme_info.get("accent_color", COLOR_ACCENT)

        # Configure Window
        self.title("OpenBird")
        self.geometry("1180x780")
        self.minsize(980, 640)
        ctk.set_appearance_mode("dark")
        self.configure(fg_color=COLOR_BG)

        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass

        self._load_icon()
        self._build_header()
        self._build_main_grid()
        self._build_footer()
        self._init_tray()
        self._init_global_hotkeys()

        # Connect audio device alert callback
        self.service.on_mic_muted_detected = self._on_mic_muted_detected

        self._initial_load()
        self.after(1000, self._periodic_status_update)

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    def _load_icon(self):
        candidates = [
            os.path.join(SCRIPT_DIR, "assets", "openbird_fluent.ico")
        ]
        self.ico_path = None
        for c in candidates:
            if os.path.exists(c):
                self.ico_path = c
                try:
                    self.iconbitmap(c)
                    break
                except Exception:
                    pass

    def _build_header(self):
        header_frame = ctk.CTkFrame(self, fg_color="transparent", height=42)
        header_frame.pack(fill="x", padx=18, pady=(12, 6))

        brand_frame = ctk.CTkFrame(header_frame, fg_color="transparent")
        brand_frame.pack(side="left")

        png_path = os.path.join(SCRIPT_DIR, "assets", "openbird_fluent.png")
        self.logo_img = None
        if os.path.exists(png_path):
            try:
                pil_img = Image.open(png_path).resize((24, 24), Image.Resampling.LANCZOS)
                self.logo_img = ctk.CTkImage(pil_img, size=(24, 24))
            except Exception:
                pass

        if self.logo_img:
            ctk.CTkLabel(brand_frame, image=self.logo_img, text="").pack(side="left", padx=(0, 10))

        title_lbl = ctk.CTkLabel(
            brand_frame,
            text="OpenBird",
            font=ctk.CTkFont(family=FONT_FAMILY, size=18, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        )
        title_lbl.pack(side="left")

        self.system_status_dot = ctk.CTkLabel(
            brand_frame,
            text="● Ready",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color="#60cdff"
        )
        self.system_status_dot.pack(side="left", padx=14)

        actions_frame = ctk.CTkFrame(header_frame, fg_color="transparent")
        actions_frame.pack(side="right")

        btn_ask_ai = ctk.CTkButton(
            actions_frame,
            text="✨ Ask AI",
            width=76,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"),
            fg_color=COLOR_ACCENT,
            hover_color=COLOR_ACCENT_HOVER,
            command=self._open_ask_ai
        )
        btn_ask_ai.pack(side="left", padx=2)

        btn_timeline = ctk.CTkButton(
            actions_frame,
            text="⏱️ Timeline",
            width=86,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_timeline_scrub
        )
        btn_timeline.pack(side="left", padx=2)

        self.btn_digest = ctk.CTkButton(
            actions_frame,
            text="📊 Digest",
            width=76,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._generate_daily_digest
        )
        self.btn_digest.pack(side="left", padx=2)

        btn_calendar = ctk.CTkButton(
            actions_frame,
            text="Calendar",
            width=74,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_calendar
        )
        btn_calendar.pack(side="left", padx=2)

        btn_screen_search = ctk.CTkButton(
            actions_frame,
            text="Screen",
            width=68,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_screen_search
        )
        btn_screen_search.pack(side="left", padx=2)

        btn_health = ctk.CTkButton(
            actions_frame,
            text="Health",
            width=64,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_health_audit
        )
        btn_health.pack(side="left", padx=2)

        btn_audio_diag = ctk.CTkButton(
            actions_frame,
            text="Devices",
            width=66,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_audio_diagnostics
        )
        btn_audio_diag.pack(side="left", padx=2)

        btn_tasks_export = ctk.CTkButton(
            actions_frame,
            text="Tasks",
            width=56,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._export_tasks_tracker
        )
        btn_tasks_export.pack(side="left", padx=2)

        refresh_btn = ctk.CTkButton(
            actions_frame,
            text="Refresh",
            width=64,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._refresh_meetings_list
        )
        refresh_btn.pack(side="left", padx=2)

        vault_btn = ctk.CTkButton(
            actions_frame,
            text="Vault",
            width=54,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#282828",
            hover_color="#333333",
            command=self._open_vault_folder
        )
        vault_btn.pack(side="left", padx=2)

    def _build_main_grid(self):
        container = ctk.CTkFrame(self, fg_color="transparent")
        container.pack(fill="both", expand=True, padx=18, pady=6)

        self.left_column = ctk.CTkFrame(container, fg_color="transparent", width=290)
        self.left_column.pack(side="left", fill="y", padx=(0, 12))
        self.left_column.pack_propagate(False)

        self.right_column = ctk.CTkFrame(container, fg_color="transparent")
        self.right_column.pack(side="right", fill="both", expand=True)

        self._build_left_controls()
        self._build_right_panels()

    def _build_left_controls(self):
        sec_lbl = ctk.CTkLabel(
            self.left_column,
            text="SESSION RECORDING",
            font=ctk.CTkFont(family=FONT_FAMILY, size=11, weight="bold"),
            text_color=COLOR_TEXT_MUTED
        )
        sec_lbl.pack(anchor="w", pady=(0, 8))

        # --- 1. Primary Action Card ---
        self.rec_card = ctk.CTkFrame(
            self.left_column,
            fg_color=COLOR_CARD_BG,
            border_width=1,
            border_color=COLOR_BORDER,
            corner_radius=10
        )
        self.rec_card.pack(fill="x", pady=(0, 10), ipady=10)

        self.btn_record = ctk.CTkButton(
            self.rec_card,
            text="Start Session",
            font=ctk.CTkFont(family=FONT_FAMILY, size=14, weight="bold"),
            fg_color=COLOR_ACCENT,
            hover_color=COLOR_ACCENT_HOVER,
            text_color="#ffffff",
            height=42,
            corner_radius=8,
            command=self._on_toggle_recording
        )
        self.btn_record.pack(fill="x", padx=16, pady=(4, 6))

        self.lbl_timer = ctk.CTkLabel(
            self.rec_card,
            text="00:00:00",
            font=ctk.CTkFont(family=FONT_MONO, size=22, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        )
        self.lbl_timer.pack(pady=(0, 4))

        self.lbl_mic_alert = ctk.CTkLabel(
            self.rec_card,
            text="",
            font=ctk.CTkFont(family=FONT_TEXT, size=11, weight="bold"),
            text_color=COLOR_AMBER
        )
        self.lbl_mic_alert.pack(pady=(0, 2))

        self.audio_meter = AudioMeter(self.rec_card, num_bars=14, width=220, height=18)
        self.audio_meter.pack(pady=(0, 4))

        # Template Selector
        tpl_frame = ctk.CTkFrame(self.rec_card, fg_color="transparent")
        tpl_frame.pack(fill="x", padx=16, pady=(6, 2))
        ctk.CTkLabel(tpl_frame, text="Note Template:", font=ctk.CTkFont(family=FONT_TEXT, size=10), text_color=COLOR_TEXT_MUTED).pack(side="left")
        self.opt_template = ctk.CTkOptionMenu(
            tpl_frame,
            values=["Executive Brief", "Actions Only", "Technical Spec"],
            height=24,
            width=130,
            font=ctk.CTkFont(family=FONT_TEXT, size=10),
            command=self._on_template_selected
        )
        self.opt_template.pack(side="right")
        self.opt_template.set("Executive Brief")

        # --- 2. Screen Context Memory Card ---
        self.screen_card = ctk.CTkFrame(
            self.left_column,
            fg_color=COLOR_CARD_BG,
            border_width=1,
            border_color=COLOR_BORDER,
            corner_radius=10
        )
        self.screen_card.pack(fill="x", pady=(0, 10), ipady=8)

        screen_hdr = ctk.CTkFrame(self.screen_card, fg_color="transparent")
        screen_hdr.pack(fill="x", padx=14, pady=(4, 2))

        ctk.CTkLabel(
            screen_hdr,
            text="Screen Context",
            font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        self.screen_status_badge = ctk.CTkLabel(
            screen_hdr,
            text="ACTIVE",
            font=ctk.CTkFont(family=FONT_TEXT, size=10, weight="bold"),
            text_color=COLOR_GREEN,
            fg_color="#182e21",
            corner_radius=4,
            width=54,
            height=20
        )
        self.screen_status_badge.pack(side="right")

        self.lbl_screen_countdown = ctk.CTkLabel(
            self.screen_card,
            text="Next check in 20s",
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            text_color=COLOR_TEXT_SECONDARY
        )
        self.lbl_screen_countdown.pack(anchor="w", padx=14, pady=(2, 2))

        self.lbl_screen_active_win = ctk.CTkLabel(
            self.screen_card,
            text="Active: None",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_MUTED
        )
        self.lbl_screen_active_win.pack(anchor="w", padx=14, pady=(0, 8))

        btn_screen_frame = ctk.CTkFrame(self.screen_card, fg_color="transparent")
        btn_screen_frame.pack(fill="x", padx=14, pady=(2, 4))

        self.btn_capture_now = ctk.CTkButton(
            btn_screen_frame,
            text="Capture Now",
            width=116,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._on_force_capture
        )
        self.btn_capture_now.pack(side="left", padx=(0, 4))

        self.btn_pause_screen = ctk.CTkButton(
            btn_screen_frame,
            text="Pause",
            width=80,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._on_toggle_screen
        )
        self.btn_pause_screen.pack(side="left")

        # --- 3. Voice Activity Auto-Detection Card ---
        self.vad_card = ctk.CTkFrame(
            self.left_column,
            fg_color=COLOR_CARD_BG,
            border_width=1,
            border_color=COLOR_BORDER,
            corner_radius=10
        )
        self.vad_card.pack(fill="x", pady=(0, 10), ipady=8)

        vad_hdr = ctk.CTkFrame(self.vad_card, fg_color="transparent")
        vad_hdr.pack(fill="x", padx=14, pady=(4, 2))

        ctk.CTkLabel(
            vad_hdr,
            text="Voice Activity",
            font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        self.switch_vad = ctk.CTkSwitch(
            vad_hdr,
            text="",
            width=42,
            switch_width=38,
            switch_height=20,
            progress_color=COLOR_ACCENT,
            command=self._on_toggle_vad
        )
        self.switch_vad.pack(side="right")
        if getattr(self.service.audio, "auto_vad_enabled", True):
            self.switch_vad.select()
        else:
            self.switch_vad.deselect()

        ctk.CTkLabel(
            self.vad_card,
            text="Auto-records when speech is sustained; filters typing noise.",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_SECONDARY,
            wraplength=250,
            justify="left"
        ).pack(anchor="w", padx=14, pady=(2, 4))

        # --- 4. Journal & Vault Shortcuts ---
        shortcuts_frame = ctk.CTkFrame(self.left_column, fg_color="transparent")
        shortcuts_frame.pack(fill="x", pady=(2, 0))

        btn_daily = ctk.CTkButton(
            shortcuts_frame,
            text="Daily Journal",
            height=32,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#252525",
            hover_color="#303030",
            command=self._open_daily_journal
        )
        btn_daily.pack(side="left", fill="x", expand=True, padx=(0, 4))

        btn_meetings = ctk.CTkButton(
            shortcuts_frame,
            text="Vault Folder",
            height=32,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            fg_color="#252525",
            hover_color="#303030",
            command=self._open_vault_folder
        )
        btn_meetings.pack(side="right", fill="x", expand=True, padx=(4, 0))

    def _build_right_panels(self):
        # Top Panel: Clickable List of Recent Meeting Notes
        self.meetings_container = ctk.CTkFrame(
            self.right_column,
            fg_color=COLOR_CARD_BG,
            border_width=1,
            border_color=COLOR_BORDER,
            corner_radius=10,
            height=250
        )
        self.meetings_container.pack(fill="x", pady=(0, 10))
        self.meetings_container.pack_propagate(False)

        top_hdr = ctk.CTkFrame(self.meetings_container, fg_color="transparent", height=38)
        top_hdr.pack(fill="x", padx=14, pady=(8, 2))

        ctk.CTkLabel(
            top_hdr,
            text="Recent Sessions",
            font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        self.search_entry = ctk.CTkEntry(
            top_hdr,
            placeholder_text="Filter sessions...",
            width=200,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#181818",
            border_color=COLOR_BORDER
        )
        self.search_entry.pack(side="right")
        self.search_entry.bind("<KeyRelease>", self._on_filter_changed)

        # Tag Filter Chips Bar
        self.tag_bar = ctk.CTkFrame(self.meetings_container, fg_color="transparent", height=26)
        self.tag_bar.pack(fill="x", padx=12, pady=(0, 4))
        self._render_tag_chips()

        self.meetings_scroll = ctk.CTkScrollableFrame(
            self.meetings_container,
            fg_color="transparent"
        )
        self.meetings_scroll.pack(fill="both", expand=True, padx=8, pady=(0, 8))

        # Bottom Panel: Markdown Preview of Selected Note
        self.preview_container = ctk.CTkFrame(
            self.right_column,
            fg_color=COLOR_CARD_BG,
            border_width=1,
            border_color=COLOR_BORDER,
            corner_radius=10
        )
        self.preview_container.pack(fill="both", expand=True)

        preview_hdr = ctk.CTkFrame(self.preview_container, fg_color="transparent", height=40)
        preview_hdr.pack(fill="x", padx=16, pady=(10, 6))

        self.lbl_preview_title = ctk.CTkLabel(
            preview_hdr,
            text="Select a session above to view notes",
            font=ctk.CTkFont(family=FONT_FAMILY, size=14, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        )
        self.lbl_preview_title.pack(side="left")

        self.preview_actions = ctk.CTkFrame(preview_hdr, fg_color="transparent")
        self.preview_actions.pack(side="right")

        btn_edit_tags = ctk.CTkButton(
            self.preview_actions,
            text="Tags",
            width=50,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._open_tag_editor
        )
        btn_edit_tags.pack(side="left", padx=2)

        self.btn_clean_brief = ctk.CTkButton(
            self.preview_actions,
            text="Clean Brief",
            width=76,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._copy_clean_brief
        )
        self.btn_clean_brief.pack(side="left", padx=2)

        self.btn_export_docx = ctk.CTkButton(
            self.preview_actions,
            text="Word (.docx)",
            width=80,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._export_to_docx
        )
        self.btn_export_docx.pack(side="left", padx=2)

        self.btn_copy_note = ctk.CTkButton(
            self.preview_actions,
            text="Copy",
            width=52,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._copy_current_markdown
        )
        self.btn_copy_note.pack(side="left", padx=2)

        self.btn_open_editor = ctk.CTkButton(
            self.preview_actions,
            text="Open in Editor",
            width=92,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._open_in_editor
        )
        self.btn_open_editor.pack(side="left", padx=2)

        # Native Scrolled Textbox for Markdown Viewer
        self.textbox_preview = ctk.CTkTextbox(
            self.preview_container,
            fg_color="#181818",
            text_color="#e0e0e0",
            font=ctk.CTkFont(family=FONT_TEXT, size=13),
            corner_radius=8,
            border_width=1,
            border_color=COLOR_BORDER,
            wrap="word"
        )
        self.textbox_preview.pack(fill="both", expand=True, padx=14, pady=(0, 14))

        self._configure_markdown_tags()

    def _render_tag_chips(self):
        for w in self.tag_bar.winfo_children():
            w.destroy()

        chips = ["All"] + self.service.get_all_tags()[:6]
        for tag in chips:
            is_active = (self.selected_tag_filter is None and tag == "All") or (self.selected_tag_filter == tag)
            btn = ctk.CTkButton(
                self.tag_bar,
                text=tag,
                height=22,
                corner_radius=11,
                font=ctk.CTkFont(family=FONT_TEXT, size=10, weight="bold" if is_active else "normal"),
                fg_color=COLOR_ACCENT if is_active else "#282828",
                hover_color=COLOR_ACCENT_HOVER if is_active else "#333333",
                command=lambda t=tag: self._select_tag_filter(t)
            )
            btn.pack(side="left", padx=2)

    def _select_tag_filter(self, tag: str):
        self.selected_tag_filter = None if tag == "All" else tag
        self._render_tag_chips()
        self._refresh_meetings_list()

    def _on_template_selected(self, choice: str):
        mapping = {
            "Executive Brief": "executive",
            "Actions Only": "actions_only",
            "Technical Spec": "technical"
        }
        self.service.active_template = mapping.get(choice, "executive")

    def _open_tag_editor(self):
        if not self.selected_meeting_id:
            return
        m = self.service.storage.get_meeting(self.selected_meeting_id)
        current_tags = m.get("tags", "") if m else ""
        TagEditDialog(self, self.selected_meeting_id, current_tags, self._on_tags_saved)

    def _on_tags_saved(self, meeting_id: str, new_tags: str):
        self.service.update_meeting_tags(meeting_id, new_tags)
        self._render_tag_chips()
        self._refresh_meetings_list()

    def _configure_markdown_tags(self):
        tb = self.textbox_preview._textbox
        tb.tag_config("h1", font=(FONT_FAMILY, 16, "bold"), foreground="#60cdff", spacing1=10, spacing3=4)
        tb.tag_config("h2", font=(FONT_FAMILY, 14, "bold"), foreground="#ffffff", spacing1=8, spacing3=2)
        tb.tag_config("h3", font=(FONT_FAMILY, 12, "bold"), foreground="#cccccc", spacing1=6, spacing3=2)
        tb.tag_config("bullet", font=(FONT_TEXT, 12), foreground="#e0e0e0", lmargin1=16, lmargin2=28)
        tb.tag_config("action_pending", font=(FONT_TEXT, 12, "bold"), foreground="#0078d4")
        tb.tag_config("action_done", font=(FONT_TEXT, 12), foreground="#107c41")
        tb.tag_config("quote", font=(FONT_TEXT, 12, "italic"), foreground="#a0a0a0", lmargin1=14, lmargin2=14)
        tb.tag_config("code", font=(FONT_MONO, 11), foreground="#dcdcaa", background="#222222")
        tb.tag_config("meta", font=(FONT_TEXT, 11), foreground="#888888")
        tb.tag_config("alert_note", font=(FONT_TEXT, 12, "bold"), foreground="#60cdff", background="#102538", lmargin1=14, lmargin2=14)
        tb.tag_config("alert_imp", font=(FONT_TEXT, 12, "bold"), foreground="#b4a0ff", background="#261b3d", lmargin1=14, lmargin2=14)
        tb.tag_config("alert_warn", font=(FONT_TEXT, 12, "bold"), foreground="#ffaa44", background="#362210", lmargin1=14, lmargin2=14)
        tb.tag_config("alert_tip", font=(FONT_TEXT, 12, "bold"), foreground="#4cc38a", background="#122d1e", lmargin1=14, lmargin2=14)
        tb.tag_config("table_row", font=(FONT_MONO, 11), foreground="#dcdcdc", background="#1e1e1e")

    def _build_footer(self):
        footer_frame = ctk.CTkFrame(self, fg_color="transparent", height=30)
        footer_frame.pack(fill="x", padx=18, pady=(4, 16))

        self.lbl_footer_status = ctk.CTkLabel(
            footer_frame,
            text="Next screen check in 20s • Ready",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_MUTED
        )
        self.lbl_footer_status.pack(side="left")

        lbl_device = ctk.CTkLabel(
            footer_frame,
            text="OpenBird Sovereign • Hotkey: Win+Alt+R",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_MUTED
        )
        lbl_device.pack(side="right")

    # ================= System Tray & Global Hotkeys =================

    def _init_tray(self):
        try:
            import pystray
            if self.ico_path:
                ico_img = Image.open(self.ico_path).convert("RGBA").resize((64, 64))
            else:
                # No icon file shipped: draw a plain accent dot so the tray still works.
                from PIL import ImageDraw
                ico_img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
                ImageDraw.Draw(ico_img).ellipse((6, 6, 58, 58), fill=self.accent_color)
            menu = pystray.Menu(
                pystray.MenuItem("Open OpenBird", lambda icon, item: self.after(0, self._show_window), default=True),
                pystray.MenuItem("Ask OpenBird", lambda icon, item: self.after(0, self._open_ask_ai)),
                pystray.MenuItem("Timeline Replay", lambda icon, item: self.after(0, self._open_timeline_scrub)),
                pystray.MenuItem("Daily Digest", lambda icon, item: self.after(0, self._generate_daily_digest)),
                pystray.MenuItem("Toggle Session", lambda icon, item: self.after(0, self._on_toggle_recording)),
                pystray.MenuItem("Search Screen Memory", lambda icon, item: self.after(0, self._open_screen_search)),
                pystray.Menu.SEPARATOR,
                pystray.MenuItem("Exit", lambda icon, item: self.after(0, self._quit_app))
            )
            self.tray_icon = pystray.Icon("OpenBird", ico_img, "OpenBird", menu)
            threading.Thread(target=self.tray_icon.run, daemon=True).start()
        except Exception as e:
            logger.warning(f"Tray icon unavailable; closing the window will exit the app: {e}")
            self.tray_icon = None

    def _notify(self, title: str, message: str):
        if self.tray_icon:
            try:
                self.tray_icon.notify(title=title, message=message)
            except Exception:
                pass

    def _init_global_hotkeys(self):
        def hotkey_listener():
            try:
                user32 = ctypes.windll.user32
                HOTKEY_ID = 101
                spec = self.service.config.get("hotkey", "win+alt+r")
                try:
                    mods, vk = config.parse_hotkey(spec)
                except ValueError as e:
                    logger.warning(f"{e} Falling back to win+alt+r.")
                    spec = "win+alt+r"
                    mods, vk = config.parse_hotkey(spec)

                if not user32.RegisterHotKey(None, HOTKEY_ID, mods, vk):
                    logger.warning(f"Global hotkey '{spec}' is taken by another app; "
                                   f"set a different \"hotkey\" in {config.user_config_path()}.")
                else:
                    logger.info(f"Global hotkey registered: {spec}")
                    msg = wintypes.MSG()
                    while not self._is_closing:
                        if user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
                            if msg.message == 0x0312 and msg.wParam == HOTKEY_ID:
                                self.after(0, self._on_toggle_recording)
                            user32.TranslateMessage(ctypes.byref(msg))
                            user32.DispatchMessageW(ctypes.byref(msg))
                        time.sleep(0.05)
                    user32.UnregisterHotKey(None, HOTKEY_ID)
            except Exception as e:
                logger.warning(f"Global hotkey listener stopped: {e}")

        threading.Thread(target=hotkey_listener, daemon=True).start()

    def _show_window(self):
        self.deiconify()
        self.lift()
        self.focus_force()

    def _on_close(self):
        if self.tray_icon:
            self.withdraw()
        else:
            self._quit_app()

    def _quit_app(self):
        self._is_closing = True
        if self.tray_icon:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        self.destroy()

    # ================= UI Actions & Event Handlers =================

    def _initial_load(self):
        self._render_tag_chips()
        self._refresh_meetings_list()
        self._update_status_display()

    def _open_ask_ai(self):
        AskOpenBirdDialog(self, self.service)

    def _open_timeline_scrub(self):
        TimelineScrubDialog(self, self.service)

    def _open_health_audit(self):
        HealthDialog(self, self.service)

    def _generate_daily_digest(self):
        self.btn_digest.configure(text="Rollup...", state="disabled")
        def worker():
            res = self.service.generate_daily_digest()
            self.after(0, lambda: self._on_digest_created(res))
        threading.Thread(target=worker, daemon=True).start()

    def _on_digest_created(self, res: Dict):
        self.btn_digest.configure(text="📊 Digest", state="normal")
        self._notify("OpenBird Executive Digest", f"Daily Digest generated ({res.get('meetings_count', 0)} sessions).")
        self._refresh_meetings_list()
        self.lbl_preview_title.configure(text=f"Daily Digest: {res.get('date')}")
        self._render_styled_markdown(res.get("markdown_content", ""))

    def _on_mic_muted_detected(self):
        self.after(0, lambda: self.lbl_mic_alert.configure(text="⚠️ Hardware Mic Muted or Zero Signal"))
        self._notify("OpenBird Audio Alert", "Microphone appears hardware-muted or unassigned.")

    def _open_calendar(self):
        CalendarDialog(self, self.service)

    def _open_screen_search(self):
        ScreenSearchDialog(self, self.service)

    def _open_audio_diagnostics(self):
        AudioDiagnosticsDialog(self, self.service)

    def _export_tasks_tracker(self):
        path = self.service.export_all_tasks()
        if os.path.exists(path):
            subprocess.Popen(f'notepad.exe "{path}"', shell=True)

    def _export_to_docx(self):
        if not self.selected_meeting_id:
            return
        path = self.service.export_meeting_docx(self.selected_meeting_id)
        if path and os.path.exists(path):
            self.btn_export_docx.configure(text="Exported!")
            self.after(1500, lambda: self.btn_export_docx.configure(text="Word (.docx)"))
            subprocess.Popen(f'explorer "{path}"', shell=True)

    def _on_toggle_recording(self):
        if not self.service.audio.is_recording:
            self.lbl_mic_alert.configure(text="")
            self.service.start_meeting(title="Manual Session")
            self.btn_record.configure(
                text="Stop & Synthesize",
                fg_color=COLOR_RECORDING,
                hover_color=COLOR_RECORDING_HOVER
            )
            self.rec_card.configure(border_color=COLOR_RECORDING)
            self.system_status_dot.configure(text="● Recording", text_color=COLOR_RECORDING)
            self._notify("OpenBird", "Session recording started.")
        else:
            self.btn_record.configure(
                text="Synthesizing...",
                state="disabled",
                fg_color="#363636"
            )
            self.system_status_dot.configure(text="● Synthesizing", text_color="#f2c80f")
            self._notify("OpenBird", "Transcribing & synthesizing notes...")

            def worker():
                try:
                    res = self.service.stop_meeting(wait=True)
                except Exception as e:
                    logger.error(f"Error stopping session: {e}", exc_info=True)
                    res = None
                self.after(0, lambda: self._on_recording_completed(res))

            threading.Thread(target=worker, daemon=True).start()

    def _on_recording_completed(self, res):
        self.btn_record.configure(
            text="Start Session",
            state="normal",
            fg_color=COLOR_ACCENT,
            hover_color=COLOR_ACCENT_HOVER
        )
        self.rec_card.configure(border_color=COLOR_BORDER)
        self.system_status_dot.configure(text="● Ready", text_color="#60cdff")
        self.lbl_timer.configure(text="00:00:00")
        self._refresh_meetings_list()

        if res and res.get("meeting_id"):
            self._select_meeting(res["meeting_id"])
            if res.get("warning"):
                self._notify("OpenBird", f"Saved without transcript: {res['warning']}")
            else:
                self._notify("OpenBird", f"Notes ready: {res.get('title', 'Session')}")

    def _on_toggle_screen(self):
        is_active = self.service.toggle_screen_capture()
        if is_active:
            self.screen_status_badge.configure(text="ACTIVE", text_color=COLOR_GREEN, fg_color="#182e21")
            self.btn_pause_screen.configure(text="Pause")
        else:
            self.screen_status_badge.configure(text="PAUSED", text_color=COLOR_AMBER, fg_color="#3a2518")
            self.btn_pause_screen.configure(text="Resume")

    def _on_force_capture(self):
        threading.Thread(target=self.service.force_screen_capture, daemon=True).start()
        self.btn_capture_now.configure(text="Captured!", state="disabled")
        self.after(1500, lambda: self.btn_capture_now.configure(text="Capture Now", state="normal"))

    def _on_toggle_vad(self):
        self.service.audio.auto_vad_enabled = bool(self.switch_vad.get())

    def _open_daily_journal(self):
        t_dir = getattr(self.service.screen_watcher, "timeline_dir", "")
        today_file = os.path.join(t_dir, f"{time.strftime('%Y-%m-%d')}_daily_context.md")
        if os.path.exists(today_file):
            subprocess.Popen(f'notepad.exe "{today_file}"', shell=True)
        elif t_dir and os.path.exists(t_dir):
            subprocess.Popen(f'explorer "{t_dir}"', shell=True)

    def _open_vault_folder(self):
        v_dir = self.service.storage.markdown_dir
        if os.path.exists(v_dir):
            subprocess.Popen(f'explorer "{v_dir}"', shell=True)

    def _copy_current_markdown(self):
        text = self.textbox_preview.get("1.0", "end-1c")
        if text:
            self.clipboard_clear()
            self.clipboard_append(text)
            self.btn_copy_note.configure(text="Copied!")
            self.after(1500, lambda: self.btn_copy_note.configure(text="Copy"))

    def _copy_clean_brief(self):
        if not self.selected_meeting_id:
            return
        m = self.service.storage.get_meeting(self.selected_meeting_id)
        if not m:
            return
        raw_md = self.service.get_meeting_markdown(self.selected_meeting_id) or ""
        clean_lines = []
        skip = False
        for line in raw_md.splitlines():
            if line.startswith("## Visual & Screen Context Timeline") or line.startswith("## Full Transcript"):
                skip = True
            elif line.startswith("## ") and skip:
                skip = False
            if not skip:
                clean_lines.append(line)
        clean_text = "\n".join(clean_lines).strip()
        self.clipboard_clear()
        self.clipboard_append(clean_text)
        self.btn_clean_brief.configure(text="Brief Copied!")
        self.after(1500, lambda: self.btn_clean_brief.configure(text="Clean Brief"))

    def _open_in_editor(self):
        if not self.selected_meeting_id:
            return
        m = self.service.storage.get_meeting(self.selected_meeting_id)
        if m and m.get("markdown_path") and os.path.exists(m["markdown_path"]):
            subprocess.Popen(f'notepad.exe "{m["markdown_path"]}"', shell=True)

    def _on_filter_changed(self, event=None):
        if self._search_debounce_job:
            self.after_cancel(self._search_debounce_job)
        self._search_debounce_job = self.after(150, self._apply_search_filter)

    def _apply_search_filter(self):
        q = self.search_entry.get().strip().lower()
        self._render_meetings_list(q)

    # ================= Meetings List & Markdown Renderer =================

    def _refresh_meetings_list(self):
        self.all_meetings = self.service.get_meetings_list(tag=self.selected_tag_filter)
        self._render_meetings_list(self.search_entry.get().strip().lower())

        if not self.selected_meeting_id and self.all_meetings:
            self._select_meeting(self.all_meetings[0]["id"])

    def _render_meetings_list(self, filter_query: str = ""):
        for widget in self.meetings_scroll.winfo_children():
            widget.destroy()

        filtered = [
            m for m in self.all_meetings
            if filter_query in m["title"].lower() or filter_query in str(m.get("created_at", "")).lower() or filter_query in str(m.get("tags", "")).lower()
        ]

        if not filtered:
            ctk.CTkLabel(
                self.meetings_scroll,
                text="No matching sessions found.",
                font=ctk.CTkFont(family=FONT_TEXT, size=12),
                text_color=COLOR_TEXT_MUTED
            ).pack(pady=20)
            return

        for m in filtered:
            m_id = m["id"]
            is_selected = (m_id == self.selected_meeting_id)

            card = ctk.CTkFrame(
                self.meetings_scroll,
                fg_color="#282828" if is_selected else "#1f1f1f",
                border_width=1,
                border_color=COLOR_ACCENT if is_selected else "#2a2a2a",
                corner_radius=8,
                height=48
            )
            card.pack(fill="x", pady=2, padx=2)
            card.pack_propagate(False)

            info_frame = ctk.CTkFrame(card, fg_color="transparent")
            info_frame.pack(side="left", padx=10, pady=4, fill="y")

            title_lbl = ctk.CTkLabel(
                info_frame,
                text=m["title"][:38],
                font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"),
                text_color=COLOR_TEXT_PRIMARY,
                anchor="w"
            )
            title_lbl.pack(anchor="w")

            dur_str = f"{m.get('duration_sec', 0):.1f}s"
            tags_str = f" • #{m['tags']}" if m.get("tags") else ""
            meta_str = f"{m.get('created_at', '')[:16]} • {dur_str}{tags_str}"
            meta_lbl = ctk.CTkLabel(
                info_frame,
                text=meta_str,
                font=ctk.CTkFont(family=FONT_TEXT, size=10),
                text_color=COLOR_TEXT_MUTED,
                anchor="w"
            )
            meta_lbl.pack(anchor="w")

            actions_cnt = m.get("action_count", 0)
            if actions_cnt > 0:
                badge = ctk.CTkLabel(
                    card,
                    text=f"{actions_cnt} action" if actions_cnt == 1 else f"{actions_cnt} actions",
                    font=ctk.CTkFont(family=FONT_TEXT, size=10, weight="bold"),
                    text_color="#60cdff",
                    fg_color="#182836",
                    corner_radius=4,
                    width=60,
                    height=20
                )
                badge.pack(side="right", padx=10)

            for elem in [card, info_frame, title_lbl, meta_lbl]:
                elem.bind("<Button-1>", lambda e, mid=m_id: self._select_meeting(mid))

    def _select_meeting(self, meeting_id: str):
        self.selected_meeting_id = meeting_id
        self._render_meetings_list(self.search_entry.get().strip().lower())

        m = self.service.storage.get_meeting(meeting_id)
        if not m:
            return

        self.lbl_preview_title.configure(text=m.get("title", "Session Notes"))
        md_text = self.service.get_meeting_markdown(meeting_id) or "No markdown notes found."

        self._render_styled_markdown(md_text)

    def _render_styled_markdown(self, md: str):
        tb = self.textbox_preview._textbox
        tb.configure(state="normal")
        tb.delete("1.0", "end")

        for line in md.splitlines():
            line_str = line.strip()
            if line_str.startswith("# "):
                tb.insert("end", line_str[2:] + "\n", "h1")
            elif line_str.startswith("## "):
                tb.insert("end", line_str[3:] + "\n", "h2")
            elif line_str.startswith("### "):
                tb.insert("end", line_str[4:] + "\n", "h3")
            elif line_str.startswith("> [!NOTE]"):
                tb.insert("end", "  ℹ️ NOTE: " + line_str[9:].strip() + "\n", "alert_note")
            elif line_str.startswith("> [!IMPORTANT]"):
                tb.insert("end", "  ⚡ IMPORTANT: " + line_str[14:].strip() + "\n", "alert_imp")
            elif line_str.startswith("> [!WARNING]") or line_str.startswith("> [!CAUTION]"):
                tag_name = line_str.split("]")[0][3:]
                tb.insert("end", f"  ⚠️ {tag_name}: " + line_str[len(tag_name)+4:].strip() + "\n", "alert_warn")
            elif line_str.startswith("> [!TIP]"):
                tb.insert("end", "  💡 TIP: " + line_str[8:].strip() + "\n", "alert_tip")
            elif line_str.startswith("|") and line_str.endswith("|"):
                tb.insert("end", "  " + line + "\n", "table_row")
            elif line_str.startswith("- [ ]") or line_str.startswith("* [ ]"):
                tb.insert("end", "  ☐  ", "action_pending")
                tb.insert("end", line_str[5:] + "\n", "bullet")
            elif line_str.startswith("- [x]") or line_str.startswith("* [x]"):
                tb.insert("end", "  ☑  ", "action_done")
                tb.insert("end", line_str[5:] + "\n", "bullet")
            elif line_str.startswith("- ") or line_str.startswith("* "):
                tb.insert("end", "  • " + line_str[2:] + "\n", "bullet")
            elif line_str.startswith(">"):
                tb.insert("end", "  " + line_str[1:].strip() + "\n", "quote")
            elif line_str.startswith("```"):
                tb.insert("end", line + "\n", "meta")
            else:
                tb.insert("end", line + "\n")

        tb.configure(state="disabled")

    # ================= Status Poller (In-Memory 1Hz) =================

    def _periodic_status_update(self):
        if self._is_closing:
            return

        self._update_status_display()
        self.after(1000, self._periodic_status_update)

    def _update_status_display(self):
        is_rec = self.service.audio.is_recording
        elapsed = time.time() - self.service.audio.meeting_start_time if is_rec else 0.0

        if is_rec:
            hours = int(elapsed // 3600)
            mins = int((elapsed % 3600) // 60)
            secs = int(elapsed % 60)
            self.lbl_timer.configure(text=f"{hours:02d}:{mins:02d}:{secs:02d}")

        # Update Audio Equalizer
        mic_rms = getattr(self.service.audio, "current_mic_rms", 0.0)
        self.audio_meter.draw_bars(mic_rms, is_rec)

        # Screen watcher info
        sw = self.service.screen_watcher
        if sw.is_paused:
            self.lbl_screen_countdown.configure(text="Paused by user")
            self.screen_status_badge.configure(text="PAUSED", text_color=COLOR_AMBER, fg_color="#3a2518")
            self.btn_pause_screen.configure(text="Resume")
        else:
            rem = sw.get_seconds_until_next()
            self.lbl_screen_countdown.configure(text=f"Next check in {rem}s")
            self.screen_status_badge.configure(text="ACTIVE", text_color=COLOR_GREEN, fg_color="#182e21")
            self.btn_pause_screen.configure(text="Pause")

        # Active Window
        win_title = sw.last_capture_window or "None"
        if len(win_title) > 28:
            win_title = win_title[:28] + "..."
        self.lbl_screen_active_win.configure(text=f"Active: {win_title}")

        # Footer Status Ticker
        if is_rec:
            self.lbl_footer_status.configure(text=f"Session recording active ({int(elapsed)}s) • Transcribing on stop")
        elif sw.is_paused:
            self.lbl_footer_status.configure(text="Screen OCR Paused • Audio VAD Listening")
        else:
            rem = sw.get_seconds_until_next()
            self.lbl_footer_status.configure(text=f"Next screen check in {rem}s • Ready")


def run_gui():
    ensure_single_instance()
    logger.info(f"OpenBird {config.APP_VERSION} starting | Python {platform.python_version()} "
                f"{platform.machine()} | {sys.executable}")
    service = None
    try:
        # If the window is not up within 45 s, write every thread's stack to crash.log (hang diagnosis).
        if _crash_file:
            faulthandler.dump_traceback_later(45, file=_crash_file)
        service = MeetingService()
        logger.info("Service ready; building window.")
        app = OpenBirdNativeApp(service)
        app.report_callback_exception = lambda et, ev, tb: _log_uncaught(et, ev, tb)
        app.update()
        logger.info("Window built; starting background engines.")
        if _crash_file:
            faulthandler.cancel_dump_traceback_later()
        # Engines start after the window exists, so a capture problem cannot take the window down unseen.
        service.start_background_engines()
        app.mainloop()
        logger.info("Main loop exited normally.")
    except Exception:
        logger.critical("OpenBird failed to start:\n" + traceback.format_exc())
        _show_fatal(f"OpenBird hit an error while starting.\n\nDetails are in:\n{log_file}")
    finally:
        if service is not None:
            try:
                service.stop_background_engines()
            except Exception as e:
                logger.warning(f"Engine shutdown error: {e}")


def run_setup():
    """First-run setup: creates the settings file and downloads the speech model."""
    from openbird import models
    s = config.load_settings()
    print(f"Settings file: {config.user_config_path()}")
    model_dir = s["whisper_model_dir"]
    if models.is_model_installed(model_dir):
        print(f"Speech model already installed: {model_dir}")
        return

    def progress(done, total):
        if total:
            print(f"\rDownloading speech model: {done * 100 // total:3d}% of {total // (1024 * 1024)} MB", end="", flush=True)

    print(f"Speech model not found in {model_dir}")
    try:
        models.install_model(model_dir, progress=progress)
        print(f"\nSpeech model installed: {model_dir}")
    except Exception as e:
        print(f"\nModel download failed: {e}")
        sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description="OpenBird: 100% Native Windows 11 Desktop App.")
    parser.add_argument("command", nargs="?", default="gui", help="Command to run (gui, list, record, setup, paths)")
    parser.add_argument("--duration", type=int, help="Optional duration in seconds for CLI recording")

    args = parser.parse_args()

    if args.command == "gui":
        run_gui()
    elif args.command == "setup":
        run_setup()
    elif args.command == "paths":
        s = config.load_settings()
        print(f"Settings file : {config.user_config_path()}")
        print(f"Logs          : {config.log_dir()}")
        for key in ("db_path", "storage_dir", "recordings_dir", "screenshots_dir", "whisper_model_dir"):
            print(f"{key:<14}: {s[key]}")
        print(f"Cloud AI      : {'on' if s.get('cloud_ai_enabled') else 'off (local only)'}")
    elif args.command == "list":
        service = MeetingService()
        meetings = service.storage.list_meetings()
        print(f"\nFound {len(meetings)} session(s):")
        for m in meetings:
            print(f"- [{m['created_at']}] {m['title']} ({m['duration_sec']:.1f}s)")
    elif args.command == "record":
        service = MeetingService()
        print("Starting recording... Press Ctrl+C to stop.")
        service.start_meeting(title="Ad-hoc Session")
        try:
            if args.duration:
                time.sleep(args.duration)
            else:
                while True:
                    time.sleep(1)
        except KeyboardInterrupt:
            pass
        res = service.stop_meeting(wait=True)
        print(f"Recorded & saved to: {res.get('markdown_path')}")
    else:
        run_gui()


if __name__ == "__main__":
    main()
