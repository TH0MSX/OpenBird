"""
OpenBird - Intelligent Visual Memory & Meeting Q&A Engine ("Ask OpenBird")
Queries 24/7 Screen OCR history, Meeting Transcripts, and Action Items with Semantic Synthesis.
"""

import os
import re
import time
import json
import logging
import urllib.request
import urllib.error

from . import config
from typing import Dict, List, Optional, Tuple

logger = logging.getLogger("openbird.ask_engine")


class AskEngine:
    """Intelligent cross-memory search and answer synthesizer."""

    def __init__(self, storage, screen_watcher, gemini_api_key: str = "", groq_api_key: str = ""):
        self.storage = storage
        self.screen_watcher = screen_watcher
        self.gemini_api_key = gemini_api_key
        self.groq_api_key = groq_api_key

    def ask(self, question: str, max_results: int = 8) -> Dict:
        """
        Executes an intelligent multi-source query across:
        1. 24/7 Screen OCR visual timeline
        2. Meeting transcripts and summaries
        3. Action items & task trackers
        Returns a structured dictionary with answer markdown and cited sources.
        """
        q = question.strip()
        if not q:
            return {
                "question": question,
                "answer": "Please provide a search question or topic.",
                "sources": [],
                "meeting_matches": 0,
                "screen_matches": 0
            }

        t0 = time.time()
        # 1. Search Meeting Transcripts & Summaries
        meeting_hits = self._search_meetings(q, limit=max_results)

        # 2. Search 24/7 Screen Memory
        screen_hits = self._search_screen(q, limit=max_results)

        # 3. Search Action Items
        action_hits = self._search_actions(q, limit=max_results)

        sources = []
        for m in meeting_hits:
            sources.append({
                "type": "meeting",
                "title": m["title"],
                "timestamp": m.get("created_at", ""),
                "snippet": m["snippet"],
                "meeting_id": m.get("id"),
                "markdown_path": m.get("markdown_path")
            })

        for s in screen_hits:
            sources.append({
                "type": "screen",
                "title": s["window_title"],
                "timestamp": s.get("time_str", ""),
                "snippet": s["snippet"],
                "image_path": s.get("image_path")
            })

        for a in action_hits:
            sources.append({
                "type": "action",
                "title": f"Action: {a['task'][:40]}",
                "timestamp": a.get("created_at", ""),
                "snippet": f"Owner: {a.get('owner')} | Status: {a.get('status')} | {a.get('task')}",
                "meeting_id": a.get("meeting_id")
            })

        # 4. Generate Synthesized Executive Answer
        if (self.gemini_api_key or self.groq_api_key) and sources:
            answer = self._synthesize_ai(q, sources)
        else:
            answer = self._synthesize_extractive(q, meeting_hits, screen_hits, action_hits)

        elapsed = time.time() - t0
        logger.info(f"Query '{q[:30]}' resolved in {elapsed:.2f}s (Meetings: {len(meeting_hits)}, Screen: {len(screen_hits)}, Actions: {len(action_hits)})")

        return {
            "question": q,
            "answer": answer,
            "sources": sources,
            "elapsed_sec": round(elapsed, 2),
            "meeting_matches": len(meeting_hits),
            "screen_matches": len(screen_hits),
            "action_matches": len(action_hits)
        }

    def _search_meetings(self, query: str, limit: int = 6) -> List[Dict]:
        hits = []
        terms = [t for t in re.split(r"\s+", query) if len(t) > 2]
        if not terms:
            terms = [query]

        with self.storage._get_conn() as conn:
            # Check transcripts
            for t in terms[:3]:
                cur = conn.execute("""
                    SELECT t.meeting_id, t.speaker, t.timestamp_str, t.text, m.title, m.created_at, m.markdown_path
                    FROM transcripts t
                    JOIN meetings m ON t.meeting_id = m.id
                    WHERE t.text LIKE ?
                    ORDER BY t.id DESC LIMIT ?
                """, (f"%{t}%", limit))
                for row in cur.fetchall():
                    r = dict(row)
                    hits.append({
                        "id": r["meeting_id"],
                        "title": f"Meeting: {r['title']}",
                        "created_at": r.get("created_at", ""),
                        "snippet": f"[{r.get('timestamp_str', '00:00')}] {r.get('speaker', 'Speaker')}: {r.get('text', '')}",
                        "markdown_path": r.get("markdown_path")
                    })

            # Check meeting titles and tags
            cur2 = conn.execute("""
                SELECT id, title, created_at, tags, markdown_path
                FROM meetings
                WHERE title LIKE ? OR tags LIKE ?
                ORDER BY created_at DESC LIMIT ?
            """, (f"%{query}%", f"%{query}%", limit))
            for row in cur2.fetchall():
                r = dict(row)
                hits.append({
                    "id": r["id"],
                    "title": f"Session: {r['title']}",
                    "created_at": r.get("created_at", ""),
                    "snippet": f"Tags: {r.get('tags', 'General')}",
                    "markdown_path": r.get("markdown_path")
                })

        # Deduplicate
        seen = set()
        deduped = []
        for h in hits:
            key = (h.get("id"), h.get("snippet")[:40])
            if key not in seen:
                seen.add(key)
                deduped.append(h)
        return deduped[:limit]

    def _search_screen(self, query: str, limit: int = 6) -> List[Dict]:
        if not self.screen_watcher:
            return []
        raw_hits = self.screen_watcher.search_timeline(query, limit=limit)
        results = []
        for r in raw_hits:
            ocr = r.get("ocr_text", "")
            # Find snippet window around query term
            pos = ocr.lower().find(query.lower())
            if pos != -1:
                start = max(0, pos - 60)
                end = min(len(ocr), pos + 120)
                snippet = "..." + ocr[start:end].replace("\n", " ").strip() + "..."
            else:
                snippet = ocr[:140].replace("\n", " ").strip() + "..."

            results.append({
                "window_title": r.get("window_title", "Unknown App"),
                "time_str": r.get("time_str", ""),
                "snippet": snippet,
                "image_path": r.get("image_path")
            })
        return results

    def _search_actions(self, query: str, limit: int = 5) -> List[Dict]:
        hits = []
        with self.storage._get_conn() as conn:
            cur = conn.execute("""
                SELECT a.id, a.meeting_id, a.owner, a.task, a.status, a.created_at, m.title as meeting_title
                FROM action_items a
                LEFT JOIN meetings m ON a.meeting_id = m.id
                WHERE a.task LIKE ? OR a.owner LIKE ?
                ORDER BY a.created_at DESC LIMIT ?
            """, (f"%{query}%", f"%{query}%", limit))
            for row in cur.fetchall():
                hits.append(dict(row))
        return hits

    def _synthesize_extractive(self, question: str, meeting_hits: List[Dict], screen_hits: List[Dict], action_hits: List[Dict]) -> str:
        """High-density deterministic extractive summary when operating 100% offline."""
        if not meeting_hits and not screen_hits and not action_hits:
            return f"### No Direct Matches Found\n\nNo records matched **\"{question}\"** across your meetings or 24/7 screen timeline.\n\n*Tip: Try broader search keywords or check the Vault folder.*"

        lines = [f"### Memory Query Results for: \"{question}\"\n"]

        if meeting_hits:
            lines.append("#### 🎙️ Relevant Meeting Discussions")
            for m in meeting_hits[:4]:
                lines.append(f"- **{m['title']}** ({m.get('created_at', '')[:16]}):")
                lines.append(f"  > {m['snippet']}")
            lines.append("")

        if action_hits:
            lines.append("#### 📋 Tracked Action Items")
            for a in action_hits[:4]:
                icon = "✅" if a.get("status") == "completed" else "⏳"
                lines.append(f"- {icon} **{a.get('owner') or config.user_name()}**: {a.get('task')} *(Meeting: {a.get('meeting_title', 'General')})*")
            lines.append("")

        if screen_hits:
            lines.append("#### 🖥️ Visual Screen Memory Timeline")
            for s in screen_hits[:4]:
                lines.append(f"- **{s['window_title']}** at `{s['time_str']}`:")
                lines.append(f"  `{s['snippet']}`")
            lines.append("")

        lines.append("---")
        lines.append(f"*Found {len(meeting_hits)} meeting notes, {len(action_hits)} action items, and {len(screen_hits)} visual timeline references.*")
        return "\n".join(lines)

    def _synthesize_ai(self, question: str, sources: List[Dict]) -> str:
        """Synthesizes context using Gemini / Groq with exact citations."""
        context_blocks = []
        for idx, s in enumerate(sources, start=1):
            context_blocks.append(f"[{idx}] Source: {s['type']} | Title: {s['title']} | Time: {s['timestamp']}\nExcerpt: {s['snippet']}")

        context_text = "\n\n".join(context_blocks)
        prompt = (
            f"You are OpenBird, {config.user_name()}'s Windows 11 AI assistant.\n"
            f"Answer the user's question directly based on their visual screen memory and meeting recordings.\n"
            f"Question: \"{question}\"\n\n"
            f"Available Evidence Context:\n{context_text}\n\n"
            f"Instructions:\n"
            f"1. Be direct, executive, concise. No conversational fluff.\n"
            f"2. Reference source numbers [1], [2] when asserting facts.\n"
            f"3. Highlight decisions, pending tasks, or timestamps explicitly.\n"
        )

        # Attempt Groq first if key present
        if self.groq_api_key:
            try:
                ans = self._call_groq(prompt)
                if ans:
                    return ans
            except Exception as e:
                logger.warning(f"Groq query failed: {e}")

        # Attempt Gemini
        if self.gemini_api_key:
            try:
                ans = self._call_gemini(prompt)
                if ans:
                    return ans
            except Exception as e:
                logger.warning(f"Gemini query failed: {e}")

        # Fallback to extractive
        return self._synthesize_extractive(question, [s for s in sources if s['type'] == 'meeting'], [s for s in sources if s['type'] == 'screen'], [s for s in sources if s['type'] == 'action'])

    def _call_groq(self, prompt: str) -> Optional[str]:
        url = "https://api.groq.com/openai/v1/chat/completions"
        headers = {
            "Authorization": f"Bearer {self.groq_api_key}",
            "Content-Type": "application/json",
            "User-Agent": "OpenBird/2.5"
        }
        data = {
            "model": config.settings().get("groq_model", "llama-3.3-70b-versatile"),
            "messages": [{"role": "user", "content": prompt}],
            "temperature": 0.2,
            "max_tokens": 1000
        }
        req = urllib.request.Request(url, data=json.dumps(data).encode("utf-8"), headers=headers, method="POST")
        with urllib.request.urlopen(req, timeout=12) as response:
            res_data = json.loads(response.read().decode("utf-8"))
            return res_data["choices"][0]["message"]["content"]

    def _call_gemini(self, prompt: str) -> Optional[str]:
        model = config.settings().get("gemini_model", "gemini-2.5-flash")
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
        # Key travels in a header, never in the URL.
        headers = {"Content-Type": "application/json", "User-Agent": "OpenBird/2.5", "x-goog-api-key": self.gemini_api_key}
        data = {
            "contents": [{"parts": [{"text": prompt}]}],
            "generationConfig": {"temperature": 0.2, "maxOutputTokens": 1000}
        }
        req = urllib.request.Request(url, data=json.dumps(data).encode("utf-8"), headers=headers, method="POST")
        with urllib.request.urlopen(req, timeout=12) as response:
            res_data = json.loads(response.read().decode("utf-8"))
            return res_data["candidates"][0]["content"]["parts"][0]["text"]
