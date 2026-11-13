"""
OpenBird - Core Meeting Service Orchestrator
Connects WASAPI Audio, WinRT OCR, Calendar Sync, Multi-Template Synthesis, and SQLite WAL.
"""

import os
import sys
import time
import json
import logging
import threading
import winreg
from typing import Dict, Optional, List

from .audio_recorder import AudioRecorder
from .transcriber import Transcriber
from .screen_ocr import ScreenContextTracker
from .screen_watcher import ScreenWatcher247
from .synthesizer import Synthesizer
from .storage import Storage
from .exporter import export_to_docx, export_to_txt, export_tasks_markdown
from .calendar_sync import CalendarManager
from .ask_engine import AskEngine
from .digest import DigestGenerator
from .health import HealthAuditor
from . import config

logger = logging.getLogger("openbird.service")


def get_system_theme_settings() -> Dict:
    """Reads Windows 11 system accent color and dark/light mode preference from Registry."""
    theme = {"dark_mode": True, "accent_color": "#0078d4"}
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
        val, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        theme["dark_mode"] = (val == 0)
    except Exception:
        pass

    try:
        key_dwm = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\DWM")
        val, _ = winreg.QueryValueEx(key_dwm, "AccentColor")
        abgr = f"{val:08x}"
        # Convert ABGR to RGB
        theme["accent_color"] = "#" + abgr[6:8] + abgr[4:6] + abgr[2:4]
    except Exception:
        pass

    return theme


class MeetingService:
    def __init__(self, config_path: str = None):
        # Settings live in %LOCALAPPDATA%\\OpenBird\\config.json (see config.py).
        self.config = config.load_settings(config_path)

        db_path = self.config["db_path"]
        storage_dir = self.config["storage_dir"]
        screenshots_dir = self.config["screenshots_dir"]
        recordings_dir = self.config["recordings_dir"]
        for d in (os.path.dirname(db_path), storage_dir, screenshots_dir, recordings_dir):
            os.makedirs(d, exist_ok=True)

        self.storage = Storage(db_path=db_path, markdown_dir=storage_dir)
        self.screen = ScreenContextTracker(screenshots_dir=screenshots_dir)
        self._transcriber = None
        self.active_template = self.config.get("default_template", "executive")
        self.synthesizer = Synthesizer(
            gemini_api_key=self.config.get("gemini_api_key", ""),
            groq_api_key=self.config.get("groq_api_key", "")
        )

        # Calendar manager
        calendar_dir = os.path.join(storage_dir, "calendars")
        self.calendar = CalendarManager(calendar_dir=calendar_dir)

        # 24/7 Screen Watcher
        timeline_dir = os.path.join(storage_dir, "timeline")
        self.screen_watcher = ScreenWatcher247(
            db_path=self.storage.db_path,
            screenshots_dir=screenshots_dir,
            timeline_dir=timeline_dir,
            interval_sec=self.config.get("screen_ocr_interval_seconds", 20),
            retention_days=self.config.get("screenshot_retention_days", 7)
        )

        # Unified Audio Engine
        self.audio = AudioRecorder(
            sample_rate=self.config.get("sample_rate", 16000),
            output_dir=recordings_dir,
            speech_threshold=self.config.get("auto_transcribe_threshold", 0.015),
            min_speech_sec=self.config.get("min_speech_seconds", 1.5),
            silence_timeout_sec=self.config.get("silence_timeout_seconds", 25.0)
        )
        self.audio.auto_vad_enabled = self.config.get("auto_transcribe", True)
        self.audio.on_meeting_started = self._on_audio_meeting_started
        self.audio.on_meeting_finished = self._on_audio_meeting_finished
        self.audio.on_mic_muted_warning = self._on_mic_muted

        # Ask Engine & Digest Generator
        self.ask_engine = AskEngine(
            storage=self.storage,
            screen_watcher=self.screen_watcher,
            gemini_api_key=self.config.get("gemini_api_key", ""),
            groq_api_key=self.config.get("groq_api_key", "")
        )
        self.digest_generator = DigestGenerator(
            storage=self.storage,
            screen_watcher=self.screen_watcher,
            output_dir=os.path.join(storage_dir, "digests")
        )

        self.on_meeting_processed = None
        self.on_mic_muted_detected = None
        self.current_meeting_info = None

    def _on_mic_muted(self):
        logger.warning("Service received mic muted alert from audio recorder.")
        if self.on_mic_muted_detected:
            try:
                self.on_mic_muted_detected()
            except Exception as e:
                logger.warning(f"Mic-muted callback failed: {e}")

    def _on_audio_meeting_started(self, meeting_id: str, title: str):
        # 1. Check if an active calendar appointment matches
        cal_event = self.calendar.get_current_or_upcoming_meeting()
        tags = ""
        if cal_event:
            title = cal_event["summary"]
            tags = "Calendar"
            if "acme" in title.lower():
                tags += ", Client-Acme"
        else:
            active_app = self.screen.get_active_window_title()
            if config.foreground_is_excluded():
                active_app = "Unknown"  # never put a private window's title into a meeting name
            if active_app not in ["Desktop / Inactive", "Unknown"]:
                title = f"Meeting ({active_app[:40]})"

        self.current_meeting_info = {
            "id": meeting_id,
            "title": title,
            "tags": tags,
            "start_time": time.time()
        }
        logger.info(f"Meeting session active: '{title}' ({meeting_id}) [Tags: {tags}]")

    def _on_audio_meeting_finished(self, meeting_data: dict):
        try:
            meeting_id = meeting_data["meeting_id"]
            title = self.current_meeting_info["title"] if self.current_meeting_info else meeting_data["title"]
            tags = self.current_meeting_info.get("tags", "") if self.current_meeting_info else ""
            start_time = meeting_data.get("start_time", time.time() - meeting_data.get("duration", 0))
            duration_sec = meeting_data.get("duration", 0.0)
            wav_path = meeting_data.get("wav_path")

            self.current_meeting_info = None

            logger.info(f"Transcribing completed meeting: '{title}' ({duration_sec:.1f}s)...")

            dialogue = []
            warning = None
            if wav_path and os.path.exists(wav_path) and duration_sec > 1.0:
                try:
                    dialogue = self.transcriber.transcribe_dual_channel(wav_path)
                except FileNotFoundError as e:
                    # The recording is kept; notes are saved without a transcript.
                    warning = str(e)
                    logger.error(f"Transcription skipped: {e}")

            # Query screen timeline for context
            screen_frames = []
            with self.storage._get_conn() as conn:
                cur = conn.execute("""
                    SELECT timestamp, time_str, window_title, image_path, ocr_text
                    FROM screen_timeline
                    WHERE timestamp >= ? AND timestamp <= ?
                    ORDER BY timestamp ASC
                """, (start_time, time.time()))
                screen_frames = [dict(r) for r in cur.fetchall()]

            summary_data = self.synthesizer.generate_summary(
                meeting_name=title,
                dialogue=dialogue,
                screen_context=screen_frames,
                template=self.active_template
            )
            markdown_text = self.synthesizer.render_markdown(summary_data, dialogue, screen_context=screen_frames)

            md_path = self.storage.save_meeting(
                meeting_id=meeting_id,
                title=title,
                duration_sec=duration_sec,
                wav_path=wav_path,
                summary_data=summary_data,
                dialogue=dialogue,
                screen_context=screen_frames,
                markdown_text=markdown_text,
                tags=tags
            )

            res = {
                "status": "completed",
                "meeting_id": meeting_id,
                "title": title,
                "duration_sec": duration_sec,
                "markdown_path": md_path,
                "summary": summary_data,
                "turns_count": len(dialogue),
                "tags": tags,
                "warning": warning
            }

            if self.on_meeting_processed:
                try:
                    self.on_meeting_processed(res)
                except Exception as e:
                    logger.warning(f"on_meeting_processed callback error: {e}")

            return res

        except Exception as e:
            logger.error(f"Error processing completed meeting: {e}", exc_info=True)
            return {"status": "error", "error": str(e)}

    @property
    def transcriber(self):
        if self._transcriber is None:
            self._transcriber = Transcriber(
                model_dir=self.config["whisper_model_dir"],
                num_threads=self.config.get("whisper_threads", 4)
            )
        return self._transcriber

    def start_background_engines(self):
        self.screen_watcher.start()
        self.audio.start_engine()

    def stop_background_engines(self):
        self.screen_watcher.stop()
        self.audio.stop_engine()

    def toggle_screen_capture(self) -> bool:
        return self.screen_watcher.toggle()

    def toggle_auto_listen(self) -> bool:
        self.audio.auto_vad_enabled = not self.audio.auto_vad_enabled
        return self.audio.auto_vad_enabled

    def start_meeting(self, title: str = "Manual Meeting") -> Dict:
        self.audio.start(title=title)
        return {"status": "started", "title": title}

    def stop_meeting(self, wait: bool = False) -> Dict:
        res = self.audio.stop()
        if not res:
            return {"status": "not_recording"}
        if wait:
            return self._on_audio_meeting_finished(res)
        return {"status": "processing", "meeting_id": res.get("meeting_id")}

    def get_status(self) -> Dict:
        recording = self.audio.is_recording
        elapsed = time.time() - self.audio.meeting_start_time if recording else 0.0
        return {
            "is_recording": recording,
            "meeting_id": self.audio.current_meeting_id if recording else None,
            "title": self.audio.current_title if recording else None,
            "elapsed_seconds": elapsed,
            "mic_rms": getattr(self.audio, "current_mic_rms", 0.0),
            "loop_rms": getattr(self.audio, "current_loop_rms", 0.0),
            "screen_watcher_paused": self.screen_watcher.is_paused,
            "seconds_until_next_screen": self.screen_watcher.get_seconds_until_next(),
            "last_screen_window": self.screen_watcher.last_capture_window,
            "last_screen_time": self.screen_watcher.last_capture_time_str,
            "screen_capture_count": self.screen_watcher.capture_count,
            "screen_excluded_count": self.screen_watcher.excluded_count,
            "cloud_ai_enabled": bool(self.config.get("cloud_ai_enabled")),
            "auto_transcribe_enabled": self.audio.auto_vad_enabled,
            "active_template": self.active_template
        }

    def get_meetings_list(self, tag: str = None, limit: int = 50) -> List[Dict]:
        meetings = self.storage.list_meetings(tag=tag, limit=limit)
        with self.storage._get_conn() as conn:
            for m in meetings:
                cur = conn.execute("SELECT COUNT(*) as cnt FROM action_items WHERE meeting_id = ?", (m["id"],))
                row = cur.fetchone()
                m["action_count"] = row["cnt"] if row else 0
        return meetings

    def get_meeting_markdown(self, meeting_id: str) -> Optional[str]:
        m = self.storage.get_meeting(meeting_id)
        if not m or not m.get("markdown_path") or not os.path.exists(m["markdown_path"]):
            return None
        with open(m["markdown_path"], "r", encoding="utf-8") as f:
            return f.read()

    def force_screen_capture(self) -> Optional[Dict]:
        return self.screen_watcher.force_capture()

    def search_screen_memory(self, query: str, limit: int = 40) -> List[Dict]:
        return self.screen_watcher.search_timeline(query=query, limit=limit)

    def toggle_action(self, meeting_id: str, task_text: str, is_completed: bool) -> bool:
        return self.storage.toggle_action_item(meeting_id, task_text, is_completed)

    def update_meeting_tags(self, meeting_id: str, tags: str) -> bool:
        return self.storage.update_tags(meeting_id, tags)

    def get_all_tags(self) -> List[str]:
        return self.storage.get_all_tags()

    def export_meeting_docx(self, meeting_id: str) -> Optional[str]:
        md = self.get_meeting_markdown(meeting_id)
        m = self.storage.get_meeting(meeting_id)
        if not md or not m:
            return None
        docx_path = os.path.join(self.storage.markdown_dir, f"{meeting_id}.docx")
        return export_to_docx(md, docx_path, title=m.get("title", "Meeting Notes"))

    def export_meeting_txt(self, meeting_id: str) -> Optional[str]:
        md = self.get_meeting_markdown(meeting_id)
        if not md:
            return None
        txt_path = os.path.join(self.storage.markdown_dir, f"{meeting_id}.txt")
        return export_to_txt(md, txt_path)

    def export_all_tasks(self) -> str:
        pending = self.storage.get_pending_actions()
        tasks_path = os.path.join(self.storage.markdown_dir, "tasks.md")
        return export_tasks_markdown(pending, tasks_path)

    def get_audio_diagnostics(self) -> Dict:
        return self.audio.get_device_diagnostics()

    def get_theme_info(self) -> Dict:
        return get_system_theme_settings()

    def ask(self, question: str, max_results: int = 8) -> Dict:
        """Executes a natural language query across meetings and screen timeline."""
        return self.ask_engine.ask(question, max_results=max_results)

    def generate_daily_digest(self, target_date: str = None) -> Dict:
        """Generates an executive daily/weekly summary report."""
        return self.digest_generator.generate_daily_digest(target_date=target_date)

    def audit_system_health(self) -> Dict:
        """Runs the complete subsystem health and environment audit."""
        model_dir = self.config["whisper_model_dir"]
        return HealthAuditor.audit_system(
            db_path=self.storage.db_path,
            model_dir=model_dir,
            vault_dir=self.storage.markdown_dir
        )

    def get_timeline_entries(self, date_str: str = None, limit: int = 150) -> List[Dict]:
        """Retrieves chronological timeline frames for scrub bar navigation."""
        return self.screen_watcher.get_timeline_entries(date_str=date_str, limit=limit)
