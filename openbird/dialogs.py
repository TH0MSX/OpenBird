"""
OpenBird - Native Fluent 2 Dialogs
Contains Modal Dialogs for Natural Language Query, Visual Timeline Scrubbing, System Health, Calendar, and Tagging.
"""

import os
import sys
import time
import subprocess
import threading
from typing import Optional, List, Dict

import customtkinter as ctk
import pywinstyles
from PIL import Image

from . import config

# Fluent 2 Color Constants
COLOR_BG = "#181818"
COLOR_CARD_BG = "#222222"
COLOR_CARD_HOVER = "#2a2a2a"
COLOR_BORDER = "#303030"
COLOR_ACCENT = "#0078d4"
COLOR_ACCENT_HOVER = "#0084c7"
COLOR_RECORDING = "#e81123"
COLOR_TEXT_PRIMARY = "#ffffff"
COLOR_TEXT_SECONDARY = "#9e9e9e"
COLOR_TEXT_MUTED = "#6a6a6a"
COLOR_GREEN = "#107c41"
COLOR_AMBER = "#d83b01"

FONT_FAMILY = "Segoe UI Variable Display"
FONT_TEXT = "Segoe UI Variable Text"
FONT_MONO = "Cascadia Code"


class CalendarDialog(ctk.CTkToplevel):
    """Calendar Sync & Upcoming Appointments Dialog."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("Calendar Appointments & Auto-Tagging")
        self.geometry("640x480")
        self.minsize(560, 380)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass
        self._build_ui()

    def _build_ui(self):
        top = ctk.CTkFrame(self, fg_color="transparent")
        top.pack(fill="x", padx=16, pady=12)

        ctk.CTkLabel(
            top,
            text="Calendar Integration",
            font=ctk.CTkFont(family=FONT_FAMILY, size=16, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        btn_folder = ctk.CTkButton(
            top,
            text="Open Calendars Folder",
            width=140,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=lambda: subprocess.Popen(f'explorer "{self.service.calendar.calendar_dir}"', shell=True)
        )
        btn_folder.pack(side="right")

        ctk.CTkLabel(
            self,
            text="Drop .ics calendar exports into the folder to automatically tag active meetings with titles and attendees.",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_SECONDARY,
            wraplength=600,
            justify="left"
        ).pack(anchor="w", padx=16, pady=(0, 10))

        self.scroll = ctk.CTkScrollableFrame(self, fg_color="transparent")
        self.scroll.pack(fill="both", expand=True, padx=16, pady=(0, 16))

        events = self.service.calendar.scan_all_calendars()
        if not events:
            ctk.CTkLabel(
                self.scroll,
                text="No scheduled appointments found.\nAdd .ics calendar files or use the quick scheduler below.",
                font=ctk.CTkFont(family=FONT_TEXT, size=12),
                text_color=COLOR_TEXT_MUTED
            ).pack(pady=40)
        else:
            for ev in events:
                card = ctk.CTkFrame(self.scroll, fg_color=COLOR_CARD_BG, corner_radius=8, border_width=1, border_color=COLOR_BORDER)
                card.pack(fill="x", pady=4)
                ctk.CTkLabel(card, text=ev["summary"], font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"), text_color=COLOR_TEXT_PRIMARY).pack(anchor="w", padx=12, pady=(6, 2))
                time_info = f"{ev['start'].strftime('%Y-%m-%d %H:%M')} - {ev['end'].strftime('%H:%M')}"
                ctk.CTkLabel(card, text=time_info, font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color="#60cdff").pack(anchor="w", padx=12, pady=(0, 6))


class TagEditDialog(ctk.CTkToplevel):
    """Dialog to add or update meeting tags."""
    def __init__(self, master, meeting_id: str, current_tags: str, on_saved):
        super().__init__(master)
        self.meeting_id = meeting_id
        self.on_saved = on_saved
        self.title("Edit Session Tags")
        self.geometry("420x220")
        self.resizable(False, False)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass

        ctk.CTkLabel(self, text="Session Tags", font=ctk.CTkFont(family=FONT_FAMILY, size=15, weight="bold")).pack(anchor="w", padx=18, pady=(16, 4))
        ctk.CTkLabel(self, text="Comma-separated labels (e.g. Client-Acme, 1on1, Personal):", font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color=COLOR_TEXT_MUTED).pack(anchor="w", padx=18, pady=(0, 8))

        self.entry = ctk.CTkEntry(self, width=380, height=36, corner_radius=6, font=ctk.CTkFont(family=FONT_TEXT, size=12))
        self.entry.pack(padx=18, pady=(0, 14))
        self.entry.insert(0, current_tags)

        btn_row = ctk.CTkFrame(self, fg_color="transparent")
        btn_row.pack(fill="x", padx=18, pady=(0, 12))

        ctk.CTkButton(btn_row, text="Save", width=80, height=30, fg_color=COLOR_ACCENT, hover_color=COLOR_ACCENT_HOVER, command=self._save).pack(side="right", padx=(6, 0))
        ctk.CTkButton(btn_row, text="Cancel", width=80, height=30, fg_color="#2c2c2c", hover_color="#383838", command=self.destroy).pack(side="right")

    def _save(self):
        new_tags = self.entry.get().strip()
        self.on_saved(self.meeting_id, new_tags)
        self.destroy()


class ScreenSearchDialog(ctk.CTkToplevel):
    """Full-Text Visual Memory Search Dialog across 24/7 WinRT OCR History."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("Visual Memory & Screen Timeline Search")
        self.geometry("860x580")
        self.minsize(720, 480)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass
        self._build_ui()
        self.after(100, lambda: self.search_entry.focus_set())

    def _build_ui(self):
        top_frame = ctk.CTkFrame(self, fg_color="transparent")
        top_frame.pack(fill="x", padx=16, pady=12)

        ctk.CTkLabel(
            top_frame,
            text="Screen Context Search",
            font=ctk.CTkFont(family=FONT_FAMILY, size=16, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        self.lbl_count = ctk.CTkLabel(
            top_frame,
            text="Indexed visual frames available",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_MUTED
        )
        self.lbl_count.pack(side="right")

        search_bar = ctk.CTkFrame(self, fg_color="transparent")
        search_bar.pack(fill="x", padx=16, pady=(0, 10))

        self.search_entry = ctk.CTkEntry(
            search_bar,
            placeholder_text="Search text from browser tabs, documents, spreadsheets, or meetings...",
            height=36,
            corner_radius=8,
            font=ctk.CTkFont(family=FONT_TEXT, size=13),
            fg_color="#222222",
            border_color=COLOR_BORDER
        )
        self.search_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        self.search_entry.bind("<Return>", lambda e: self._do_search())

        btn_find = ctk.CTkButton(
            search_bar,
            text="Search",
            width=80,
            height=36,
            corner_radius=8,
            font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"),
            fg_color=COLOR_ACCENT,
            hover_color=COLOR_ACCENT_HOVER,
            command=self._do_search
        )
        btn_find.pack(side="right")

        self.results_scroll = ctk.CTkScrollableFrame(self, fg_color="transparent")
        self.results_scroll.pack(fill="both", expand=True, padx=16, pady=(0, 16))

        ctk.CTkLabel(
            self.results_scroll,
            text="Enter keywords above to search all 24/7 on-screen activity.",
            font=ctk.CTkFont(family=FONT_TEXT, size=13),
            text_color=COLOR_TEXT_MUTED
        ).pack(pady=40)

    def _do_search(self):
        query = self.search_entry.get().strip()
        if not query:
            return

        for w in self.results_scroll.winfo_children():
            w.destroy()

        results = self.service.search_screen_memory(query, limit=50)
        self.lbl_count.configure(text=f"Found {len(results)} match(es)")

        if not results:
            ctk.CTkLabel(
                self.results_scroll,
                text=f"No screen context matched '{query}'.",
                font=ctk.CTkFont(family=FONT_TEXT, size=13),
                text_color=COLOR_TEXT_MUTED
            ).pack(pady=40)
            return

        for r in results:
            card = ctk.CTkFrame(
                self.results_scroll,
                fg_color=COLOR_CARD_BG,
                border_width=1,
                border_color=COLOR_BORDER,
                corner_radius=8
            )
            card.pack(fill="x", pady=4, padx=2)

            chdr = ctk.CTkFrame(card, fg_color="transparent")
            chdr.pack(fill="x", padx=12, pady=(8, 4))

            ctk.CTkLabel(
                chdr,
                text=r.get("window_title", "Unknown Window"),
                font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"),
                text_color=COLOR_TEXT_PRIMARY
            ).pack(side="left")

            ctk.CTkLabel(
                chdr,
                text=r.get("time_str", ""),
                font=ctk.CTkFont(family=FONT_MONO, size=10),
                text_color=COLOR_TEXT_MUTED
            ).pack(side="right")

            snippet = r.get("ocr_text", "").replace("\n", " ").strip()
            if len(snippet) > 280:
                snippet = snippet[:280] + "..."

            body = ctk.CTkLabel(
                card,
                text=snippet if snippet else "[No text detected]",
                font=ctk.CTkFont(family=FONT_TEXT, size=11),
                text_color="#c8c8c8",
                wraplength=780,
                justify="left"
            )
            body.pack(anchor="w", padx=12, pady=(0, 8))

            cfoot = ctk.CTkFrame(card, fg_color="transparent")
            cfoot.pack(fill="x", padx=12, pady=(0, 8))

            img_path = r.get("image_path", "")
            if img_path and os.path.exists(img_path):
                btn_img = ctk.CTkButton(
                    cfoot,
                    text="Open Snapshot",
                    width=96,
                    height=24,
                    corner_radius=4,
                    font=ctk.CTkFont(family=FONT_TEXT, size=10),
                    fg_color="#2c2c2c",
                    hover_color="#383838",
                    command=lambda p=img_path: subprocess.Popen(f'explorer "{p}"', shell=True)
                )
                btn_img.pack(side="left", padx=(0, 6))

            btn_copy = ctk.CTkButton(
                cfoot,
                text="Copy Text",
                width=80,
                height=24,
                corner_radius=4,
                font=ctk.CTkFont(family=FONT_TEXT, size=10),
                fg_color="#2c2c2c",
                hover_color="#383838",
                command=lambda t=r.get("ocr_text", ""): self._copy_to_clip(t)
            )
            btn_copy.pack(side="left")

    def _copy_to_clip(self, text: str):
        self.clipboard_clear()
        self.clipboard_append(text)


class AudioDiagnosticsDialog(ctk.CTkToplevel):
    """Audio Device Hardware Inspection Dialog."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("Audio Device Diagnostics")
        self.geometry("620x460")
        self.minsize(540, 380)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass
        self._build_ui()

    def _build_ui(self):
        diag = self.service.get_audio_diagnostics()

        ctk.CTkLabel(
            self,
            text="WASAPI Audio Diagnostics",
            font=ctk.CTkFont(family=FONT_FAMILY, size=16, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(anchor="w", padx=18, pady=(16, 10))

        frame = ctk.CTkFrame(self, fg_color=COLOR_CARD_BG, corner_radius=10, border_width=1, border_color=COLOR_BORDER)
        frame.pack(fill="both", expand=True, padx=18, pady=(0, 16), ipady=10)

        ctk.CTkLabel(frame, text=f"Active Microphone ({config.user_name()}):", font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"), text_color="#60cdff").pack(anchor="w", padx=16, pady=(10, 2))
        ctk.CTkLabel(frame, text=diag.get("default_mic", "None"), font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color=COLOR_TEXT_PRIMARY).pack(anchor="w", padx=16, pady=(0, 8))

        ctk.CTkLabel(frame, text="Active Speaker Loopback (Meeting Callers):", font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"), text_color="#60cdff").pack(anchor="w", padx=16, pady=(4, 2))
        ctk.CTkLabel(frame, text=diag.get("default_speaker", "None"), font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color=COLOR_TEXT_PRIMARY).pack(anchor="w", padx=16, pady=(0, 8))

        ctk.CTkLabel(frame, text="All Detected Input Devices:", font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"), text_color=COLOR_TEXT_MUTED).pack(anchor="w", padx=16, pady=(6, 2))
        for m in diag.get("microphones", []):
            ctk.CTkLabel(frame, text=f"• {m}", font=ctk.CTkFont(family=FONT_TEXT, size=10), text_color=COLOR_TEXT_SECONDARY).pack(anchor="w", padx=24, pady=1)

        ctk.CTkLabel(frame, text="All Loopback Devices:", font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"), text_color=COLOR_TEXT_MUTED).pack(anchor="w", padx=16, pady=(6, 2))
        for lb in diag.get("loopbacks", []):
            ctk.CTkLabel(frame, text=f"• {lb}", font=ctk.CTkFont(family=FONT_TEXT, size=10), text_color=COLOR_TEXT_SECONDARY).pack(anchor="w", padx=24, pady=1)

        ctk.CTkButton(
            self,
            text="Close",
            width=100,
            height=32,
            corner_radius=6,
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self.destroy
        ).pack(anchor="e", padx=18, pady=(0, 16))


# ==================== V2.4 NEW DIALOGS ====================

class AskOpenBirdDialog(ctk.CTkToplevel):
    """Local Semantic Search & Visual Q&A Engine ('Ask OpenBird') [Feature 1]."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("Ask OpenBird - Sovereign Executive Intelligence")
        self.geometry("880x640")
        self.minsize(720, 500)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass
        self._build_ui()
        self.after(100, lambda: self.query_entry.focus_set())

    def _build_ui(self):
        top_frame = ctk.CTkFrame(self, fg_color="transparent")
        top_frame.pack(fill="x", padx=18, pady=14)

        ctk.CTkLabel(
            top_frame,
            text="✨ Ask OpenBird",
            font=ctk.CTkFont(family=FONT_FAMILY, size=18, weight="bold"),
            text_color="#60cdff"
        ).pack(side="left")

        self.lbl_stats = ctk.CTkLabel(
            top_frame,
            text="Queries meetings, action items, and 24/7 screen timeline",
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            text_color=COLOR_TEXT_MUTED
        )
        self.lbl_stats.pack(side="right")

        search_bar = ctk.CTkFrame(self, fg_color="transparent")
        search_bar.pack(fill="x", padx=18, pady=(0, 12))

        self.query_entry = ctk.CTkEntry(
            search_bar,
            placeholder_text="Ask any question about your workday, decisions, Acme meetings, or on-screen text...",
            height=38,
            corner_radius=8,
            font=ctk.CTkFont(family=FONT_TEXT, size=13),
            fg_color="#222222",
            border_color=COLOR_BORDER
        )
        self.query_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
        self.query_entry.bind("<Return>", lambda e: self._on_ask())

        self.btn_ask = ctk.CTkButton(
            search_bar,
            text="Ask OpenBird",
            width=110,
            height=38,
            corner_radius=8,
            font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"),
            fg_color=COLOR_ACCENT,
            hover_color=COLOR_ACCENT_HOVER,
            command=self._on_ask
        )
        self.btn_ask.pack(side="right")

        # Answer Presentation Area
        self.textbox_answer = ctk.CTkTextbox(
            self,
            fg_color="#1c1c1c",
            text_color="#e6e6e6",
            font=ctk.CTkFont(family=FONT_TEXT, size=13),
            corner_radius=8,
            border_width=1,
            border_color=COLOR_BORDER,
            wrap="word",
            height=280
        )
        self.textbox_answer.pack(fill="both", expand=True, padx=18, pady=(0, 10))
        self.textbox_answer.insert("1.0", "Type a question above and press Ask OpenBird.\nExample queries:\n• 'What did we agree with Acme on dispatch procedures?'\n• 'Find what was on my screen regarding cosmetic orders'\n• 'Show all open tasks assigned to Alex'")
        self.textbox_answer.configure(state="disabled")

        # Sources / References Strip
        lbl_src_hdr = ctk.CTkLabel(
            self,
            text="CITED SOURCES & AUDIT EVIDENCE",
            font=ctk.CTkFont(family=FONT_FAMILY, size=10, weight="bold"),
            text_color=COLOR_TEXT_MUTED
        )
        lbl_src_hdr.pack(anchor="w", padx=18, pady=(2, 4))

        self.sources_scroll = ctk.CTkScrollableFrame(self, fg_color="transparent", height=130)
        self.sources_scroll.pack(fill="x", padx=18, pady=(0, 14))

    def _on_ask(self):
        q = self.query_entry.get().strip()
        if not q:
            return

        self.btn_ask.configure(text="Synthesizing...", state="disabled")
        self.lbl_stats.configure(text="Scanning visual memory & transcripts...")

        def worker():
            res = self.service.ask(q)
            self.after(0, lambda: self._display_result(res))

        threading.Thread(target=worker, daemon=True).start()

    def _display_result(self, res: Dict):
        self.btn_ask.configure(text="Ask OpenBird", state="normal")
        self.lbl_stats.configure(text=f"Resolved in {res.get('elapsed_sec', 0.0)}s • {res.get('meeting_matches', 0)} mtgs, {res.get('screen_matches', 0)} frames")

        tb = self.textbox_answer
        tb.configure(state="normal")
        tb.delete("1.0", "end")
        tb.insert("1.0", res.get("answer", "No response generated."))
        tb.configure(state="disabled")

        for w in self.sources_scroll.winfo_children():
            w.destroy()

        sources = res.get("sources", [])
        if not sources:
            ctk.CTkLabel(self.sources_scroll, text="No external source excerpts matched.", font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color=COLOR_TEXT_MUTED).pack(anchor="w")
            return

        for s in sources[:8]:
            card = ctk.CTkFrame(self.sources_scroll, fg_color=COLOR_CARD_BG, corner_radius=6, border_width=1, border_color=COLOR_BORDER)
            card.pack(fill="x", pady=2)

            chdr = ctk.CTkFrame(card, fg_color="transparent")
            chdr.pack(fill="x", padx=8, pady=(4, 2))

            icon = "🎙️" if s["type"] == "meeting" else ("🖥️" if s["type"] == "screen" else "📋")
            ctk.CTkLabel(chdr, text=f"{icon} {s['title']}", font=ctk.CTkFont(family=FONT_FAMILY, size=11, weight="bold"), text_color=COLOR_TEXT_PRIMARY).pack(side="left")
            ctk.CTkLabel(chdr, text=s.get("timestamp", ""), font=ctk.CTkFont(family=FONT_MONO, size=9), text_color=COLOR_TEXT_MUTED).pack(side="right")

            snippet = s.get("snippet", "").replace("\n", " ").strip()
            if len(snippet) > 160:
                snippet = snippet[:160] + "..."
            ctk.CTkLabel(card, text=snippet, font=ctk.CTkFont(family=FONT_TEXT, size=10), text_color="#bbbbbb", wraplength=800, justify="left").pack(anchor="w", padx=8, pady=(0, 4))


class TimelineScrubDialog(ctk.CTkToplevel):
    """Visual Timeline Scrub Bar & 24/7 Screen Replay Browser [Feature 2]."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("Timeline Activity Scrub Bar & Screen Replay")
        self.geometry("920x640")
        self.minsize(760, 520)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass

        self.frames = self.service.get_timeline_entries(limit=150)
        self.current_idx = len(self.frames) - 1 if self.frames else 0
        self._build_ui()

    def _build_ui(self):
        top_frame = ctk.CTkFrame(self, fg_color="transparent")
        top_frame.pack(fill="x", padx=18, pady=12)

        ctk.CTkLabel(
            top_frame,
            text="⏱️ Visual Timeline Replay",
            font=ctk.CTkFont(family=FONT_FAMILY, size=18, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        self.lbl_frame_counter = ctk.CTkLabel(
            top_frame,
            text=f"Frame {self.current_idx + 1} of {len(self.frames)}" if self.frames else "No timeline frames",
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            text_color="#60cdff"
        )
        self.lbl_frame_counter.pack(side="right")

        # Scrub Control Bar
        scrub_frame = ctk.CTkFrame(self, fg_color=COLOR_CARD_BG, corner_radius=10, border_width=1, border_color=COLOR_BORDER)
        scrub_frame.pack(fill="x", padx=18, pady=(0, 12), ipady=6)

        ctrl_row = ctk.CTkFrame(scrub_frame, fg_color="transparent")
        ctrl_row.pack(fill="x", padx=14, pady=(6, 2))

        btn_prev = ctk.CTkButton(
            ctrl_row,
            text="◀ Previous",
            width=80,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._on_prev_frame
        )
        btn_prev.pack(side="left")

        max_val = max(1, len(self.frames) - 1)
        self.slider = ctk.CTkSlider(
            ctrl_row,
            from_=0,
            to=max_val,
            number_of_steps=max_val,
            height=18,
            command=self._on_slider_scrub
        )
        self.slider.pack(side="left", fill="x", expand=True, padx=12)
        self.slider.set(self.current_idx)

        btn_next = ctk.CTkButton(
            ctrl_row,
            text="Next ▶",
            width=80,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._on_next_frame
        )
        btn_next.pack(side="right")

        # Display details of selected frame
        self.detail_card = ctk.CTkFrame(self, fg_color=COLOR_CARD_BG, corner_radius=10, border_width=1, border_color=COLOR_BORDER)
        self.detail_card.pack(fill="both", expand=True, padx=18, pady=(0, 14), ipady=8)

        card_hdr = ctk.CTkFrame(self.detail_card, fg_color="transparent")
        card_hdr.pack(fill="x", padx=16, pady=(12, 4))

        self.lbl_time = ctk.CTkLabel(
            card_hdr,
            text="Timestamp: --",
            font=ctk.CTkFont(family=FONT_MONO, size=13, weight="bold"),
            text_color="#60cdff"
        )
        self.lbl_time.pack(side="left")

        self.btn_snapshot = ctk.CTkButton(
            card_hdr,
            text="Open Screenshot",
            width=110,
            height=26,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._open_current_snapshot
        )
        self.btn_snapshot.pack(side="right")

        self.lbl_window = ctk.CTkLabel(
            self.detail_card,
            text="Active Application: None",
            font=ctk.CTkFont(family=FONT_FAMILY, size=14, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        )
        self.lbl_window.pack(anchor="w", padx=16, pady=(2, 8))

        ctk.CTkLabel(
            self.detail_card,
            text="Detected On-Screen OCR Text:",
            font=ctk.CTkFont(family=FONT_TEXT, size=11, weight="bold"),
            text_color=COLOR_TEXT_MUTED
        ).pack(anchor="w", padx=16, pady=(0, 2))

        self.ocr_box = ctk.CTkTextbox(
            self.detail_card,
            fg_color="#181818",
            text_color="#e0e0e0",
            font=ctk.CTkFont(family=FONT_TEXT, size=12),
            corner_radius=8,
            border_width=1,
            border_color=COLOR_BORDER,
            wrap="word"
        )
        self.ocr_box.pack(fill="both", expand=True, padx=16, pady=(0, 12))

        # Bottom Actions
        bottom_bar = ctk.CTkFrame(self, fg_color="transparent")
        bottom_bar.pack(fill="x", padx=18, pady=(0, 14))

        btn_copy = ctk.CTkButton(
            bottom_bar,
            text="Copy Frame Text",
            width=120,
            height=30,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._copy_frame_text
        )
        btn_copy.pack(side="left")

        btn_journal = ctk.CTkButton(
            bottom_bar,
            text="Open Daily Journal (Markdown)",
            width=190,
            height=30,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=lambda: subprocess.Popen(f'explorer "{self.service.screen_watcher.timeline_dir}"', shell=True)
        )
        btn_journal.pack(side="left", padx=8)

        self._render_current_frame()

    def _on_slider_scrub(self, val):
        self.current_idx = int(round(float(val)))
        self._render_current_frame()

    def _on_prev_frame(self):
        if self.current_idx > 0:
            self.current_idx -= 1
            self.slider.set(self.current_idx)
            self._render_current_frame()

    def _on_next_frame(self):
        if self.current_idx < len(self.frames) - 1:
            self.current_idx += 1
            self.slider.set(self.current_idx)
            self._render_current_frame()

    def _render_current_frame(self):
        if not self.frames:
            return
        frame = self.frames[self.current_idx]
        self.lbl_frame_counter.configure(text=f"Frame {self.current_idx + 1} of {len(self.frames)}")
        self.lbl_time.configure(text=f"🕒 {frame.get('time_str', 'Unknown')}")
        self.lbl_window.configure(text=f"🖥️ {frame.get('window_title', 'Desktop')}")

        tb = self.ocr_box
        tb.configure(state="normal")
        tb.delete("1.0", "end")
        ocr = frame.get("ocr_text", "")
        tb.insert("1.0", ocr if ocr else "[No text detected on screen at this frame]")
        tb.configure(state="disabled")

        img_path = frame.get("image_path")
        self.btn_snapshot.configure(state="normal" if (img_path and os.path.exists(img_path)) else "disabled")

    def _open_current_snapshot(self):
        if not self.frames:
            return
        img_path = self.frames[self.current_idx].get("image_path")
        if img_path and os.path.exists(img_path):
            subprocess.Popen(f'explorer "{img_path}"', shell=True)

    def _copy_frame_text(self):
        if not self.frames:
            return
        text = self.frames[self.current_idx].get("ocr_text", "")
        if text:
            self.clipboard_clear()
            self.clipboard_append(text)


class HealthDialog(ctk.CTkToplevel):
    """Subsystem Health & System Diagnostics Auditor [Feature 5]."""
    def __init__(self, master, service):
        super().__init__(master)
        self.service = service
        self.title("OpenBird System Health & Diagnostic Audit")
        self.geometry("740x520")
        self.minsize(620, 420)
        self.configure(fg_color=COLOR_BG)
        try:
            pywinstyles.change_header_color(self, color="#1c1c1c")
            pywinstyles.apply_style(self, "mica")
        except Exception:
            pass
        self._build_ui()

    def _build_ui(self):
        top_frame = ctk.CTkFrame(self, fg_color="transparent")
        top_frame.pack(fill="x", padx=18, pady=14)

        ctk.CTkLabel(
            top_frame,
            text="🏥 Subsystem Health & Diagnostics",
            font=ctk.CTkFont(family=FONT_FAMILY, size=18, weight="bold"),
            text_color=COLOR_TEXT_PRIMARY
        ).pack(side="left")

        btn_recheck = ctk.CTkButton(
            top_frame,
            text="Run Audit",
            width=90,
            height=28,
            corner_radius=6,
            font=ctk.CTkFont(family=FONT_TEXT, size=11),
            fg_color="#2c2c2c",
            hover_color="#383838",
            command=self._run_audit
        )
        btn_recheck.pack(side="right")

        self.scroll = ctk.CTkScrollableFrame(self, fg_color="transparent")
        self.scroll.pack(fill="both", expand=True, padx=18, pady=(0, 14))

        self._run_audit()

    def _run_audit(self):
        for w in self.scroll.winfo_children():
            w.destroy()

        res = self.service.audit_system_health()

        hdr = ctk.CTkFrame(self.scroll, fg_color=COLOR_CARD_BG, corner_radius=8, border_width=1, border_color=COLOR_BORDER)
        hdr.pack(fill="x", pady=(0, 8), padx=2, ipady=6)

        status_color = COLOR_GREEN if res["status"] == "PASS" else COLOR_AMBER
        ctk.CTkLabel(hdr, text=f"Overall Audit Status: {res['status']}", font=ctk.CTkFont(family=FONT_FAMILY, size=13, weight="bold"), text_color=status_color).pack(side="left", padx=12)
        ctk.CTkLabel(hdr, text=f"Audited: {res['timestamp']}", font=ctk.CTkFont(family=FONT_MONO, size=10), text_color=COLOR_TEXT_MUTED).pack(side="right", padx=12)

        for c in res.get("checks", []):
            card = ctk.CTkFrame(self.scroll, fg_color=COLOR_CARD_BG, corner_radius=8, border_width=1, border_color=COLOR_BORDER)
            card.pack(fill="x", pady=4, padx=2)

            chdr = ctk.CTkFrame(card, fg_color="transparent")
            chdr.pack(fill="x", padx=12, pady=(8, 2))

            ctk.CTkLabel(chdr, text=c["subsystem"], font=ctk.CTkFont(family=FONT_FAMILY, size=12, weight="bold"), text_color=COLOR_TEXT_PRIMARY).pack(side="left")

            badge_color = "#182e21" if c["status"] == "OK" else "#332314"
            badge_text_color = COLOR_GREEN if c["status"] == "OK" else COLOR_AMBER
            badge = ctk.CTkLabel(chdr, text=c["status"], font=ctk.CTkFont(family=FONT_TEXT, size=10, weight="bold"), text_color=badge_text_color, fg_color=badge_color, corner_radius=4, width=54, height=20)
            badge.pack(side="right")

            ctk.CTkLabel(card, text=c["detail"], font=ctk.CTkFont(family=FONT_TEXT, size=11), text_color=COLOR_TEXT_SECONDARY, wraplength=640, justify="left").pack(anchor="w", padx=12, pady=(0, 8))
