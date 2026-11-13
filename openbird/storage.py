"""
OpenBird - Hardened Concurrency-Safe SQLite Storage
WAL Mode, Dynamic Checkbox State Synchronization, Tags & Categorization Engine.
"""

import os
import json
import sqlite3

from . import config
import time
import logging
from typing import List, Dict, Optional

logger = logging.getLogger("openbird.storage")


class Storage:
    def __init__(self, db_path: str = None, markdown_dir: str = None):
        self.db_path = db_path or config.default_path("db_path")
        self.markdown_dir = markdown_dir or config.default_path("storage_dir")
        
        os.makedirs(os.path.dirname(self.db_path), exist_ok=True)
        os.makedirs(self.markdown_dir, exist_ok=True)
        self._init_db()

    def _get_conn(self):
        conn = sqlite3.connect(self.db_path, timeout=10.0)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode = WAL;")
        conn.execute("PRAGMA synchronous = NORMAL;")
        conn.execute("PRAGMA busy_timeout = 10000;")
        return conn

    def _execute_write(self, sql: str, params: tuple = ()):
        """Executes a write query with automatic exponential retry on database locks."""
        for attempt in range(5):
            try:
                with self._get_conn() as conn:
                    cur = conn.execute(sql, params)
                    conn.commit()
                    return cur.lastrowid
            except sqlite3.OperationalError as e:
                if "locked" in str(e).lower() or "busy" in str(e).lower():
                    time.sleep(0.05 * (2 ** attempt))
                else:
                    raise
        raise RuntimeError("SQLite database remained busy after 5 retry attempts.")

    def _init_db(self):
        with self._get_conn() as conn:
            try:
                conn.execute("PRAGMA quick_check;")
                conn.execute("PRAGMA incremental_vacuum(50);")
            except Exception:
                pass
            conn.execute("""
                CREATE TABLE IF NOT EXISTS meetings (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    duration_sec REAL,
                    wav_path TEXT,
                    markdown_path TEXT,
                    summary_json TEXT,
                    tags TEXT DEFAULT ''
                )
            """)
            try:
                conn.execute("ALTER TABLE meetings ADD COLUMN tags TEXT DEFAULT '';")
            except Exception:
                pass

            conn.execute("""
                CREATE TABLE IF NOT EXISTS transcripts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    meeting_id TEXT,
                    start_sec REAL,
                    timestamp_str TEXT,
                    speaker TEXT,
                    text TEXT,
                    FOREIGN KEY(meeting_id) REFERENCES meetings(id)
                )
            """)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS action_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    meeting_id TEXT,
                    owner TEXT,
                    task TEXT,
                    status TEXT DEFAULT 'pending',
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY(meeting_id) REFERENCES meetings(id)
                )
            """)
            conn.execute("""
                CREATE TABLE IF NOT EXISTS screen_context (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    meeting_id TEXT,
                    timestamp REAL,
                    window_title TEXT,
                    image_path TEXT,
                    ocr_text TEXT
                )
            """)
            conn.execute("CREATE INDEX IF NOT EXISTS idx_actions_meeting ON action_items(meeting_id);")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_actions_status ON action_items(status);")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_transcripts_meeting ON transcripts(meeting_id);")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_meetings_tags ON meetings(tags);")
            conn.commit()

    def save_meeting(self, meeting_id: str, title: str, duration_sec: float, wav_path: str,
                     summary_data: Dict, dialogue: List[Dict], screen_context: List[Dict], markdown_text: str, tags: str = "") -> str:
        md_filename = f"{meeting_id}_{time.strftime('%Y%m%d')}.md"
        md_path = os.path.join(self.markdown_dir, md_filename)
        
        with open(md_path, "w", encoding="utf-8") as f:
            f.write(markdown_text)

        with self._get_conn() as conn:
            conn.execute("""
                INSERT OR REPLACE INTO meetings (id, title, duration_sec, wav_path, markdown_path, summary_json, tags)
                VALUES (?, ?, ?, ?, ?, ?, ?)
            """, (meeting_id, title, duration_sec, wav_path, md_path, json.dumps(summary_data), tags))

            for d in dialogue:
                conn.execute("""
                    INSERT INTO transcripts (meeting_id, start_sec, timestamp_str, speaker, text)
                    VALUES (?, ?, ?, ?, ?)
                """, (meeting_id, d.get("start_sec", 0.0), d.get("timestamp", "00:00"), d.get("speaker", "Speaker"), d.get("text", "")))

            for ai in summary_data.get("action_items", []):
                conn.execute("""
                    INSERT INTO action_items (meeting_id, owner, task, status)
                    VALUES (?, ?, ?, ?)
                """, (meeting_id, ai.get("owner") or config.user_name(), ai.get("task", ""), ai.get("status", "pending")))

            if screen_context:
                for sc in screen_context:
                    conn.execute("""
                        INSERT INTO screen_context (meeting_id, timestamp, window_title, image_path, ocr_text)
                        VALUES (?, ?, ?, ?, ?)
                    """, (meeting_id, sc.get("timestamp", 0.0), sc.get("window_title", ""), sc.get("image_path", ""), sc.get("ocr_text", "")))

            conn.commit()

        return md_path

    def update_tags(self, meeting_id: str, tags: str) -> bool:
        """Updates tags for a meeting (e.g. 'Client-Acme, 1on1')."""
        try:
            self._execute_write("UPDATE meetings SET tags = ? WHERE id = ?", (tags, meeting_id))
            return True
        except Exception as e:
            logger.error(f"Failed to update tags for {meeting_id}: {e}")
            return False

    def get_all_tags(self) -> List[str]:
        """Extracts unique set of tags used across all meetings."""
        tags_set = set()
        with self._get_conn() as conn:
            cur = conn.execute("SELECT tags FROM meetings WHERE tags IS NOT NULL AND tags != ''")
            for r in cur.fetchall():
                for t in r["tags"].split(","):
                    clean = t.strip()
                    if clean:
                        tags_set.add(clean)
        return sorted(list(tags_set))

    def update_action_status(self, action_id: int, status: str) -> bool:
        """Toggles action item between pending and completed."""
        try:
            self._execute_write("UPDATE action_items SET status = ? WHERE id = ?", (status, action_id))
            return True
        except Exception as e:
            logger.error(f"Failed to update action {action_id}: {e}")
            return False

    def toggle_action_item(self, meeting_id: str, task_text: str, is_completed: bool) -> bool:
        """Synchronously toggles action status in SQLite and on-disk Markdown."""
        new_status = "completed" if is_completed else "pending"
        try:
            with self._get_conn() as conn:
                conn.execute("""
                    UPDATE action_items SET status = ? 
                    WHERE meeting_id = ? AND task LIKE ?
                """, (new_status, meeting_id, f"%{task_text}%"))
                conn.commit()

            meeting = self.get_meeting(meeting_id)
            if meeting and meeting.get("markdown_path") and os.path.exists(meeting["markdown_path"]):
                md_path = meeting["markdown_path"]
                with open(md_path, "r", encoding="utf-8") as f:
                    content = f.read()

                old_tag = "- [ ]" if is_completed else "- [x]"
                new_tag = "- [x]" if is_completed else "- [ ]"

                updated_lines = []
                for line in content.splitlines():
                    if task_text in line and old_tag in line:
                        line = line.replace(old_tag, new_tag, 1)
                    updated_lines.append(line)

                with open(md_path, "w", encoding="utf-8") as f:
                    f.write("\n".join(updated_lines))

            logger.info(f"Toggled action '{task_text[:30]}' to {new_status} in DB & Markdown.")
            return True
        except Exception as e:
            logger.error(f"Failed to toggle action item: {e}")
            return False

    def list_meetings(self, tag: str = None, limit: int = 50) -> List[Dict]:
        with self._get_conn() as conn:
            if tag:
                cur = conn.execute("SELECT id, title, created_at, duration_sec, markdown_path, tags FROM meetings WHERE tags LIKE ? ORDER BY created_at DESC LIMIT ?", (f"%{tag}%", limit))
            else:
                cur = conn.execute("SELECT id, title, created_at, duration_sec, markdown_path, tags FROM meetings ORDER BY created_at DESC LIMIT ?", (limit,))
            return [dict(r) for r in cur.fetchall()]

    def get_meeting(self, meeting_id: str) -> Optional[Dict]:
        with self._get_conn() as conn:
            cur = conn.execute("SELECT * FROM meetings WHERE id = ?", (meeting_id,))
            row = cur.fetchone()
            if not row:
                return None
            m = dict(row)
            m["summary"] = json.loads(m["summary_json"]) if m.get("summary_json") else {}

            cur_t = conn.execute("SELECT timestamp_str, speaker, text FROM transcripts WHERE meeting_id = ? ORDER BY start_sec ASC", (meeting_id,))
            m["transcript"] = [dict(r) for r in cur_t.fetchall()]

            cur_a = conn.execute("SELECT id, owner, task, status FROM action_items WHERE meeting_id = ?", (meeting_id,))
            m["action_items"] = [dict(r) for r in cur_a.fetchall()]
            return m

    def get_pending_actions(self) -> List[Dict]:
        with self._get_conn() as conn:
            cur = conn.execute("""
                SELECT a.id, a.meeting_id, m.title as meeting_title, a.owner, a.task, a.status, a.created_at
                FROM action_items a
                LEFT JOIN meetings m ON a.meeting_id = m.id
                WHERE a.status = 'pending'
                ORDER BY a.created_at DESC
            """)
            return [dict(r) for r in cur.fetchall()]

    def get_all_actions(self) -> List[Dict]:
        with self._get_conn() as conn:
            cur = conn.execute("""
                SELECT a.id, a.meeting_id, m.title as meeting_title, a.owner, a.task, a.status, a.created_at
                FROM action_items a
                LEFT JOIN meetings m ON a.meeting_id = m.id
                ORDER BY a.created_at DESC
            """)
            return [dict(r) for r in cur.fetchall()]

    def search(self, query: str) -> Dict:
        q = f"%{query}%"
        with self._get_conn() as conn:
            cur_m = conn.execute("SELECT id, title, created_at, markdown_path, tags FROM meetings WHERE title LIKE ? OR summary_json LIKE ? OR tags LIKE ?", (q, q, q))
            meetings = [dict(r) for r in cur_m.fetchall()]

            cur_t = conn.execute("""
                SELECT t.meeting_id, m.title as meeting_title, t.timestamp_str, t.speaker, t.text
                FROM transcripts t
                JOIN meetings m ON t.meeting_id = m.id
                WHERE t.text LIKE ?
                LIMIT 25
            """, (q,))
            transcripts = [dict(r) for r in cur_t.fetchall()]

            cur_s = conn.execute("""
                SELECT s.meeting_id, m.title as meeting_title, s.window_title, s.ocr_text
                FROM screen_context s
                JOIN meetings m ON s.meeting_id = m.id
                WHERE s.window_title LIKE ? OR s.ocr_text LIKE ?
                LIMIT 15
            """, (q, q))
            screens = [dict(r) for r in cur_s.fetchall()]

            return {
                "meetings": meetings,
                "transcripts": transcripts,
                "screen_context": screens
            }
