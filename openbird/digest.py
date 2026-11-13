"""
OpenBird - Automated Executive Digest Generator
Compiles daily and weekly operational rollups, aggregated action trackers, and screen memory insights.
"""

import os
import time
import json
import logging
from typing import Dict, List, Optional

from . import config

logger = logging.getLogger("openbird.digest")


class DigestGenerator:
    def __init__(self, storage, screen_watcher, output_dir: str = None):
        self.storage = storage
        self.screen_watcher = screen_watcher
        self.output_dir = output_dir or os.path.join(storage.markdown_dir, "digests")
        os.makedirs(self.output_dir, exist_ok=True)

    def generate_daily_digest(self, target_date: str = None) -> Dict:
        """
        Generates an executive rollup for a specific date (defaults to today 'YYYY-MM-DD').
        Aggregates meeting notes, action items, and 24/7 screen timeline focus areas.
        """
        date_str = target_date or time.strftime("%Y-%m-%d")
        t0 = time.time()

        # 1. Fetch meetings for the date
        meetings = []
        with self.storage._get_conn() as conn:
            cur = conn.execute("""
                SELECT id, title, created_at, duration_sec, tags, markdown_path, summary_json
                FROM meetings
                WHERE created_at LIKE ?
                ORDER BY created_at ASC
            """, (f"{date_str}%",))
            for r in cur.fetchall():
                m = dict(r)
                m["summary"] = json.loads(m["summary_json"]) if m.get("summary_json") else {}
                meetings.append(m)

        # 2. Fetch action items created or modified on the date
        action_items = []
        with self.storage._get_conn() as conn:
            cur = conn.execute("""
                SELECT a.id, a.meeting_id, a.owner, a.task, a.status, a.created_at, m.title as meeting_title
                FROM action_items a
                LEFT JOIN meetings m ON a.meeting_id = m.id
                WHERE a.created_at LIKE ? OR m.created_at LIKE ?
                ORDER BY a.status DESC, a.created_at ASC
            """, (f"{date_str}%", f"{date_str}%"))
            for r in cur.fetchall():
                action_items.append(dict(r))

        # 3. Fetch screen timeline stats for the date
        timeline_entries = []
        app_counts = {}
        if self.screen_watcher:
            timeline_entries = self.screen_watcher.get_timeline_entries(date_str=date_str, limit=300)
            for entry in timeline_entries:
                win = entry.get("window_title", "Desktop")
                # Group by base app name
                app = win.split(" - ")[0] if " - " in win else win
                app = app[:28]
                app_counts[app] = app_counts.get(app, 0) + 1

        # 4. Compute metrics
        total_duration_sec = sum(m.get("duration_sec", 0.0) for m in meetings)
        total_duration_min = round(total_duration_sec / 60.0, 1)
        pending_actions = [a for a in action_items if a.get("status") == "pending"]
        completed_actions = [a for a in action_items if a.get("status") == "completed"]

        # 5. Format Markdown Document
        lines = []
        lines.append(f"# 📊 OpenBird Executive Digest: {date_str}")
        lines.append(f"> **Daily Operational Rollup** | Generated on {time.strftime('%Y-%m-%d %H:%M:%S')} | Sovereign Local Intelligence\n")

        lines.append("## 📈 Executive Summary & Key Metrics")
        lines.append(f"- **Total Meetings Recorded**: `{len(meetings)}` sessions ({total_duration_min} minutes total)")
        lines.append(f"- **Action Items Tracked**: `{len(action_items)}` total (`{len(pending_actions)}` pending, `{len(completed_actions)}` completed)")
        lines.append(f"- **Visual Screen Snapshots**: `{len(timeline_entries)}` frames indexed")
        lines.append("")

        # Meetings Section
        lines.append("## 🎙️ Meeting Sessions")
        if meetings:
            lines.append("| Time | Session Title | Tags | Duration | Key Focus |")
            lines.append("| :--- | :--- | :--- | :--- | :--- |")
            for m in meetings:
                t_str = m.get("created_at", "")[11:16]
                dur_m = round(m.get("duration_sec", 0) / 60.0, 1)
                tags = m.get("tags") or "General"
                summ = m.get("summary", {}).get("executive_summary", "")
                summ_short = (summ[:75] + "...") if len(summ) > 75 else summ
                lines.append(f"| {t_str} | **{m['title']}** | `{tags}` | {dur_m}m | {summ_short} |")
        else:
            lines.append("*No dedicated audio meetings recorded on this date.*")
        lines.append("")

        # Action Items Section
        lines.append("## 📋 Action Items & Deliverables")
        if action_items:
            lines.append("### ⏳ Pending Actions")
            if pending_actions:
                for a in pending_actions:
                    lines.append(f"- [ ] **{a.get('owner') or config.user_name()}**: {a.get('task')} *(Session: {a.get('meeting_title', 'General')})*")
            else:
                lines.append("*All tracked action items for today are completed!*")
            lines.append("")

            if completed_actions:
                lines.append("### ✅ Completed Today")
                for a in completed_actions:
                    lines.append(f"- [x] **{a.get('owner') or config.user_name()}**: {a.get('task')}")
                lines.append("")
        else:
            lines.append("*No action items recorded for this date.*")
        lines.append("")

        # Screen Timeline Section
        lines.append("## 🖥️ Screen Activity & Work Focus")
        if app_counts:
            sorted_apps = sorted(app_counts.items(), key=lambda x: x[1], reverse=True)
            lines.append("Top active windows and workspaces recorded:")
            for app, count in sorted_apps[:6]:
                pct = round((count / max(1, len(timeline_entries))) * 100, 1)
                lines.append(f"- **{app}**: `{count}` frames ({pct}%)")
        else:
            lines.append("*No screen activity frames found for this date.*")
        lines.append("")

        lines.append("---")
        lines.append(f"*OpenBird Sovereign Executive Rollup • Auto-compiled in {time.time() - t0:.2f}s*")

        md_content = "\n".join(lines)
        file_name = f"{date_str}_Daily_Digest.md"
        file_path = os.path.join(self.output_dir, file_name)

        with open(file_path, "w", encoding="utf-8") as f:
            f.write(md_content)

        logger.info(f"Generated daily executive digest -> {file_path}")

        # Also attempt Word (.docx) export if python-docx is available
        docx_path = None
        try:
            from .exporter import export_to_docx
            docx_name = f"{date_str}_Daily_Digest.docx"
            docx_target = os.path.join(self.output_dir, docx_name)
            docx_path = export_to_docx(file_path, docx_target)
        except Exception as e:
            logger.debug(f"DOCX export for digest skipped: {e}")

        return {
            "date": date_str,
            "markdown_path": file_path,
            "docx_path": docx_path,
            "meetings_count": len(meetings),
            "actions_count": len(action_items),
            "pending_count": len(pending_actions),
            "completed_count": len(completed_actions),
            "markdown_content": md_content
        }
