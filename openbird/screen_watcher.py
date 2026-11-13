"""
OpenBird - 24/7 Screen Context Watcher with Non-Locking WinRT OCR
Sanitized Active Window Names, Continuous Daily Markdown Journaling, and Daily Anchor TOC.
"""

import os
import re
import time
import asyncio
import logging
import threading
import sqlite3
import ctypes
from typing import Optional, List, Dict
from PIL import ImageGrab
import winrt.windows.media.ocr as ocr
import winrt.windows.graphics.imaging as imaging
import winrt.windows.storage as storage

from . import config

logger = logging.getLogger("openbird.screen_watcher")


def clean_window_title(raw: str) -> str:
    """Strips noisy browser tails, editor marks, and notification prefixes from window titles."""
    if not raw:
        return "Desktop / Inactive"
    t = raw.strip()

    if t.startswith("● ") or t.startswith("* "):
        t = t[2:].strip()

    t = re.sub(r"^\(\d+\)\s*", "", t)

    noise_suffixes = [
        " - Google Chrome",
        " - Microsoft​ Edge",
        " - Microsoft Edge",
        " - Mozilla Firefox",
        " - Brave",
        " - Visual Studio Code",
        " - Notepad",
        " - Word",
        " - Excel",
        " - PowerPoint"
    ]
    for s in noise_suffixes:
        if t.endswith(s):
            t = t[:-len(s)].strip()
            break

    return t if t else "Desktop / Inactive"


class ScreenWatcher247:
    def __init__(self, db_path: str = None, screenshots_dir: str = None, timeline_dir: str = None, interval_sec: int = 20, retention_days: int = 7):
        self.screenshots_dir = screenshots_dir or config.default_path("screenshots_dir")
        self.db_path = db_path or config.default_path("db_path")
        self.timeline_dir = timeline_dir or os.path.join(config.default_path("storage_dir"), "timeline")
        self.interval_sec = interval_sec or 20
        self.retention_days = retention_days or 7

        os.makedirs(self.screenshots_dir, exist_ok=True)
        os.makedirs(os.path.dirname(self.db_path), exist_ok=True)
        os.makedirs(self.timeline_dir, exist_ok=True)
        self.prune_old_screenshots(self.retention_days)

        self.user32 = ctypes.windll.user32
        self.is_paused = False
        self._stop_event = threading.Event()
        self._thread = None
        self._loop = None
        self.last_window_title = ""
        self.last_ocr_snippet = ""
        self.last_capture_time_str = "Never"
        self.last_capture_window = "None"
        self.next_capture_timestamp = time.time() + self.interval_sec
        self.capture_count = 0
        self.excluded_count = 0
        self._init_db()

    def _get_conn(self):
        conn = sqlite3.connect(self.db_path, timeout=10.0)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode = WAL;")
        conn.execute("PRAGMA synchronous = NORMAL;")
        conn.execute("PRAGMA busy_timeout = 10000;")
        return conn

    def _init_db(self):
        with self._get_conn() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS screen_timeline (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp REAL,
                    time_str TEXT,
                    window_title TEXT,
                    image_path TEXT,
                    ocr_text TEXT
                )
            """)
            conn.execute("CREATE INDEX IF NOT EXISTS idx_screen_timestamp ON screen_timeline(timestamp);")
            conn.commit()

    def prune_old_screenshots(self, retention_days: int = 7):
        """Auto-prunes screenshot PNGs older than retention_days to preserve disk space."""
        try:
            if not os.path.exists(self.screenshots_dir):
                return
            cutoff = time.time() - (retention_days * 86400)
            pruned_count = 0
            for f in os.listdir(self.screenshots_dir):
                if f.lower().endswith(".png"):
                    p = os.path.join(self.screenshots_dir, f)
                    try:
                        if os.path.getmtime(p) < cutoff:
                            os.remove(p)
                            pruned_count += 1
                    except Exception:
                        pass
            if pruned_count > 0:
                logger.info(f"Storage hygiene: pruned {pruned_count} old screenshot PNGs (> {retention_days} days).")
        except Exception as e:
            logger.debug(f"Pruning note: {e}")

    def get_active_window_title(self) -> str:
        try:
            hwnd = self.user32.GetForegroundWindow()
            if not hwnd:
                return "Desktop / Inactive"
            length = self.user32.GetWindowTextLengthW(hwnd)
            if length == 0:
                return "Desktop / Inactive"
            buff = ctypes.create_unicode_buffer(length + 1)
            self.user32.GetWindowTextW(hwnd, buff, length + 1)
            raw = buff.value.strip()
            return clean_window_title(raw)
        except Exception:
            return "Unknown"

    async def _ocr_image_async(self, file_path: str) -> str:
        """Non-locking WinRT OCR processing that guarantees file handle release and memory deallocation."""
        stream = None
        bitmap = None
        decoder = None
        engine = None
        try:
            abs_path = os.path.abspath(file_path)
            file = await storage.StorageFile.get_file_from_path_async(abs_path)
            stream = await file.open_async(storage.FileAccessMode.READ)
            decoder = await imaging.BitmapDecoder.create_async(stream)
            bitmap = await decoder.get_software_bitmap_async()
            engine = ocr.OcrEngine.try_create_from_user_profile_languages()
            result = await engine.recognize_async(bitmap)
            return result.text.strip()
        except Exception as e:
            logger.warning(f"OCR error: {e}")
            return ""
        finally:
            if stream:
                try:
                    stream.close()
                except Exception:
                    pass
            del bitmap
            del decoder
            del engine

    def _append_to_daily_markdown(self, time_str: str, window_title: str, ocr_text: str, img_name: str):
        """Continuously appends screen activity to a daily Markdown journal to ensure zero context loss."""
        today_date = time.strftime("%Y-%m-%d")
        daily_md_path = os.path.join(self.timeline_dir, f"{today_date}_daily_context.md")

        is_new_file = not os.path.exists(daily_md_path)
        try:
            clean_time = time_str.split(" ")[-1] if " " in time_str else time_str
            with open(daily_md_path, "a", encoding="utf-8") as f:
                if is_new_file:
                    f.write(f"# OpenBird Daily Context & Screen Journal: {today_date}\n\n")
                    f.write("> **Visual Context Log**: Continuous active window and on-screen text log.\n\n")
                    f.write("## Daily Chronological Entries\n\n---\n\n")

                anchor_id = clean_time.replace(":", "")
                f.write(f"<a id='{anchor_id}'></a>\n")
                f.write(f"### `[{clean_time}]` {window_title}\n")
                if img_name:
                    f.write(f"*Snapshot: `{img_name}`*\n\n")

                if ocr_text:
                    preview = ocr_text[:300].replace("\n", " ").strip()
                    f.write(f"**Visible Content Preview**:\n> {preview}\n\n")
                    if len(ocr_text) > 300:
                        f.write("<details><summary>Full OCR Text (Expand)</summary>\n\n```text\n")
                        f.write(ocr_text)
                        f.write("\n```\n</details>\n\n")
                else:
                    f.write("*[No readable text detected on screen]*\n\n")
                f.write("---\n\n")
        except Exception as e:
            logger.error(f"Failed to append to daily markdown journal: {e}")

    def capture_cycle(self, force: bool = False) -> Optional[Dict]:
        if self.is_paused and not force:
            return None

        window_title = self.get_active_window_title()
        if (window_title in ["Desktop / Inactive", "Unknown", "LockApp.exe", "Windows Default Lock Screen", ""]) and not force:
            return None

        # Privacy: excluded windows (password managers, private browsing, banking)
        # are never grabbed, OCR'd or written to disk. This also applies to forced captures.
        if config.foreground_is_excluded():
            self.excluded_count += 1
            self.last_capture_window = "Excluded (private)"
            return None

        # Verify screen capture can be obtained without BitBlt failure
        try:
            screenshot = ImageGrab.grab(all_screens=bool(config.settings().get("capture_all_monitors", True)))
        except Exception as e:
            logger.debug(f"Screen grab failed (display sleeping or locked): {e}")
            return None

        # The foreground window can change between the check and the grab.
        if config.foreground_is_excluded():
            self.excluded_count += 1
            self.last_capture_window = "Excluded (private)"
            return None

        now = time.time()
        time_str = time.strftime("%Y-%m-%d %H:%M:%S")
        timestamp_slug = time.strftime("%Y%m%d_%H%M%S")
        img_name = f"screen_{timestamp_slug}.png"
        img_path = os.path.join(self.screenshots_dir, img_name)

        try:
            screenshot.save(img_path, format="PNG")
        except Exception as e:
            logger.error(f"Could not save screenshot to {img_path}: {e}")
            return None

        ocr_text = ""
        try:
            if self._loop is None or self._loop.is_closed():
                self._loop = asyncio.new_event_loop()
            ocr_text = self._loop.run_until_complete(self._ocr_image_async(img_path))
        except Exception as e:
            logger.warning(f"OCR invocation error: {e}")

        ocr_snippet = ocr_text[:120].strip()
        has_changed = (window_title != self.last_window_title) or (ocr_snippet != self.last_ocr_snippet and len(ocr_snippet) > 10)

        if force or has_changed or (now - getattr(self, "_last_stored_time", 0) > 300):
            self.last_window_title = window_title
            self.last_ocr_snippet = ocr_snippet
            self.last_capture_time_str = time_str
            self.last_capture_window = window_title
            self._last_stored_time = now
            self.capture_count += 1

            # 1. Store in SQLite with busy retry
            for attempt in range(5):
                try:
                    with self._get_conn() as conn:
                        conn.execute("""
                            INSERT INTO screen_timeline (timestamp, time_str, window_title, image_path, ocr_text)
                            VALUES (?, ?, ?, ?, ?)
                        """, (now, time_str, window_title, img_path, ocr_text))
                        conn.commit()
                    break
                except sqlite3.OperationalError as e:
                    if "locked" in str(e).lower() or "busy" in str(e).lower():
                        time.sleep(0.05 * (2 ** attempt))
                    else:
                        break

            # 2. Append to continuous Daily Markdown Journal
            self._append_to_daily_markdown(time_str, window_title, ocr_text, img_name)

            logger.info(f"Indexed screen frame [{window_title}] ({len(ocr_text)} chars) -> Daily MD journal updated.")

            return {
                "timestamp": now,
                "time_str": time_str,
                "window_title": window_title,
                "image_path": img_path,
                "ocr_text": ocr_text
            }

        return None

    def force_capture(self) -> Dict:
        """Forces an immediate screen capture and OCR indexing regardless of timer."""
        return self.capture_cycle(force=True)

    def _worker(self):
        try:
            ctypes.windll.ole32.CoInitializeEx(0, 0x0)
        except Exception:
            pass

        self._loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self._loop)

        logger.info(f"ScreenWatcher247 active (sampling every {self.interval_sec}s with continuous MD journaling).")
        while not self._stop_event.is_set():
            self.next_capture_timestamp = time.time() + self.interval_sec
            try:
                self.capture_cycle()
            except Exception as e:
                logger.warning(f"Screen cycle error: {e}", exc_info=True)
            self._stop_event.wait(self.interval_sec)

        try:
            self._loop.close()
        except Exception:
            pass

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._worker, daemon=True)
        self._thread.start()

    def stop(self):
        self._stop_event.set()
        if self._thread:
            self._thread.join(timeout=2.0)

    def pause(self):
        self.is_paused = True
        logger.info("Screen capture paused by user.")

    def resume(self):
        self.is_paused = False
        logger.info("Screen capture resumed by user.")

    def toggle(self) -> bool:
        self.is_paused = not self.is_paused
        return not self.is_paused

    def get_seconds_until_next(self) -> int:
        if self.is_paused:
            return 0
        rem = int(self.next_capture_timestamp - time.time())
        return max(0, rem)

    def search_timeline(self, query: str, limit: int = 40) -> List[Dict]:
        """Full-text search across 24/7 visual memory records."""
        q = f"%{query}%"
        with self._get_conn() as conn:
            cur = conn.execute("""
                SELECT timestamp, time_str, window_title, ocr_text, image_path
                FROM screen_timeline
                WHERE window_title LIKE ? OR ocr_text LIKE ?
                ORDER BY timestamp DESC
                LIMIT ?
            """, (q, q, limit))
            return [dict(r) for r in cur.fetchall()]

    def get_timeline_entries(self, date_str: str = None, limit: int = 150) -> List[Dict]:
        """Retrieves chronological timeline frames for scrub bar navigation."""
        with self._get_conn() as conn:
            if date_str:
                cur = conn.execute("""
                    SELECT timestamp, time_str, window_title, ocr_text, image_path
                    FROM screen_timeline
                    WHERE time_str LIKE ?
                    ORDER BY timestamp ASC
                    LIMIT ?
                """, (f"{date_str}%", limit))
            else:
                cur = conn.execute("""
                    SELECT timestamp, time_str, window_title, ocr_text, image_path
                    FROM screen_timeline
                    ORDER BY timestamp DESC
                    LIMIT ?
                """, (limit,))
                rows = [dict(r) for r in cur.fetchall()]
                rows.reverse()
                return rows
            return [dict(r) for r in cur.fetchall()]
