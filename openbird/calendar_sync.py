"""
OpenBird - Local Calendar Integration & Smart Meeting Auto-Tagging
Detects active calendar events from local .ics files and schedule appointments.
"""

import os
import re
import time
import glob
import os as _os

from . import config
import logging
from datetime import datetime, timezone, timedelta
from typing import Optional, List, Dict

logger = logging.getLogger("openbird.calendar")


class CalendarManager:
    def __init__(self, calendar_dir: str = None):
        self.calendar_dir = calendar_dir or _os.path.join(config.default_path("storage_dir"), "calendars")
        os.makedirs(self.calendar_dir, exist_ok=True)
        self.cached_events = []
        self._last_scan = 0

    def parse_ics_file(self, file_path: str) -> List[Dict]:
        """Parses standard iCalendar (.ics) format without external dependencies."""
        events = []
        if not os.path.exists(file_path):
            return events

        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                content = f.read()

            raw_events = re.findall(r"BEGIN:VEVENT(.*?)END:VEVENT", content, re.DOTALL)
            for raw in raw_events:
                summary_match = re.search(r"SUMMARY:(.+)", raw)
                summary = summary_match.group(1).strip() if summary_match else "Scheduled Meeting"

                dtstart_match = re.search(r"DTSTART(?:;[^:]+)?:(\d{8}T\d{6}Z?)", raw)
                dtend_match = re.search(r"DTEND(?:;[^:]+)?:(\d{8}T\d{6}Z?)", raw)

                attendees = re.findall(r"ATTENDEE.+?CN=([^;:\n\r]+)", raw)
                location_match = re.search(r"LOCATION:(.+)", raw)
                location = location_match.group(1).strip() if location_match else ""

                if dtstart_match:
                    start_str = dtstart_match.group(1)
                    end_str = dtend_match.group(1) if dtend_match else start_str
                    try:
                        # Parse UTC timestamp (e.g., 20261113T100000Z)
                        dt_start = datetime.strptime(start_str.replace("Z", ""), "%Y%m%dT%H%M%S")
                        dt_end = datetime.strptime(end_str.replace("Z", ""), "%Y%m%dT%H%M%S")
                        events.append({
                            "summary": summary,
                            "start": dt_start,
                            "end": dt_end,
                            "attendees": attendees,
                            "location": location,
                            "source_file": os.path.basename(file_path)
                        })
                    except Exception:
                        pass
        except Exception as e:
            logger.debug(f"ICS parsing note for {file_path}: {e}")

        return events

    def scan_all_calendars(self) -> List[Dict]:
        """Scans the calendars folder for any .ics files and aggregates upcoming events."""
        events = []
        if os.path.exists(self.calendar_dir):
            for f in glob.glob(os.path.join(self.calendar_dir, "*.ics")):
                events.extend(self.parse_ics_file(f))
        self.cached_events = events
        self._last_scan = time.time()
        return events

    def get_current_or_upcoming_meeting(self, window_minutes: int = 15) -> Optional[Dict]:
        """Finds a meeting occurring right now or starting within window_minutes."""
        if time.time() - self._last_scan > 60:
            self.scan_all_calendars()

        now = datetime.now()
        # Also check UTC if system time is in UTC
        for ev in self.cached_events:
            start = ev["start"]
            end = ev["end"]
            # Active now or within grace window
            if (start - timedelta(minutes=window_minutes)) <= now <= (end + timedelta(minutes=10)):
                return ev
        return None

    def add_manual_event(self, summary: str, duration_minutes: int = 30) -> Dict:
        """Quickly registers an ad-hoc calendar event for session tagging."""
        now = datetime.now()
        end = now + timedelta(minutes=duration_minutes)
        ev = {
            "summary": summary,
            "start": now,
            "end": end,
            "attendees": [config.user_name()],
            "location": "Virtual / Desk",
            "source_file": "manual"
        }
        self.cached_events.append(ev)
        return ev
