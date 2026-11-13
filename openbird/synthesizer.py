"""
OpenBird - Multi-Template Executive Synthesizer
Generates OpenBird-schema notes across Executive 1-Page, Actions-Only, Technical Spec, and Verbatim modes.
"""

import os
import re
import json
import logging
from typing import List, Dict, Optional
import requests

from . import config

logger = logging.getLogger("openbird.synthesizer")

TEMPLATES = {
    "executive": "Executive Brief (1-Page OpenBird Schema)",
    "actions_only": "Action Items & Decisions Only",
    "technical": "Technical Spec & Architecture Notes",
    "verbatim": "Full Verbatim Transcript"
}


class Synthesizer:
    def __init__(self, gemini_api_key: str = "", groq_api_key: str = ""):
        # Keys are resolved by config.load_settings(); empty means local-only.
        self.gemini_api_key = gemini_api_key
        self.groq_api_key = groq_api_key

    def generate_summary(self, meeting_name: str, dialogue: List[Dict], screen_context: List[Dict] = None, template: str = "executive") -> Dict:
        """
        Generates executive summary, 'For You' tracking, topics, decisions, and action items.
        Supports templates: 'executive', 'actions_only', 'technical', 'verbatim'.
        """
        template = template if template in TEMPLATES else "executive"

        if self.gemini_api_key and template != "verbatim":
            try:
                logger.info("Synthesizing meeting using Gemini Free Tier API...")
                return self._synthesize_with_gemini(meeting_name, dialogue, screen_context, template)
            except Exception as e:
                logger.warning(f"Gemini API synthesis failed, falling back to local synthesizer: {e}")

        if self.groq_api_key and template != "verbatim":
            try:
                logger.info("Synthesizing meeting using Groq Free Tier API...")
                return self._synthesize_with_groq(meeting_name, dialogue, screen_context, template)
            except Exception as e:
                logger.warning(f"Groq API synthesis failed, falling back to local synthesizer: {e}")

        logger.info(f"Synthesizing meeting using 100% Offline Local Synthesizer ({template} template)...")
        return self._synthesize_local(meeting_name, dialogue, screen_context, template)

    def _format_prompt(self, meeting_name: str, dialogue: List[Dict], screen_context: List[Dict] = None, template: str = "executive") -> str:
        transcript_text = "\n".join([f"[{d.get('timestamp', '00:00')}] {d.get('speaker', 'Speaker')}: {d.get('text', '')}" for d in dialogue])
        
        screen_text = ""
        if screen_context:
            screen_lines = []
            for s in screen_context:
                if s.get("ocr_text"):
                    screen_lines.append(f"[{s.get('time_str', '')}] App: {s.get('window_title', '')}\nText: {s.get('ocr_text', '')[:300]}")
            if screen_lines:
                screen_text = "\n\nSCREEN / SLIDE CONTEXT CAPTURED DURING CALL:\n" + "\n---\n".join(screen_lines)

        instructions = ""
        if template == "actions_only":
            instructions = "Focus strictly on actionable commitments, task owners, due dates, and concrete operational decisions."
        elif template == "technical":
            instructions = "Focus on technical architecture, systems, code, database schemas, and engineering specifications."
        else:
            instructions = "Provide a balanced, high-impact executive brief adhering to the OpenBird schema."

        prompt = f"""You are OpenBird, an AI executive partner. Analyze this meeting transcript and on-screen context for {config.user_name()}.
{instructions}

Meeting Title: {meeting_name}
Template Style: {template}

TRANSCRIPT:
{transcript_text}
{screen_text}

OUTPUT FORMAT REQUIREMENTS:
Return valid JSON with the following fields:
{{
  "title": "{meeting_name}",
  "template": "{template}",
  "tldr": "1-2 sentence executive punchline",
  "summary": "Concise paragraph overview of what transpired",
  "for_you": [
    "Specific items relevant to {config.user_name()} or tasks assigned directly to them"
  ],
  "action_items": [
    {{"owner": "Person Name", "task": "Specific concrete action", "status": "pending"}}
  ],
  "decisions": [
    "Explicit decisions agreed upon during the discussion"
  ],
  "topics": [
    {{"title": "Topic Name", "summary": "Key discussion points"}}
  ]
}}
Do NOT wrap with markdown tags if possible, or return ONLY valid JSON inside ```json```.
"""
        return prompt

    def _synthesize_with_gemini(self, meeting_name: str, dialogue: List[Dict], screen_context: List[Dict] = None, template: str = "executive") -> Dict:
        model = config.settings().get("gemini_model", "gemini-2.5-flash")
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
        prompt = self._format_prompt(meeting_name, dialogue, screen_context, template)
        payload = {
            "contents": [{"parts": [{"text": prompt}]}],
            "generationConfig": {"temperature": 0.2, "response_mime_type": "application/json"}
        }
        # Key travels in a header, never in the URL (URLs end up in logs and error messages).
        res = requests.post(url, json=payload, headers={"x-goog-api-key": self.gemini_api_key}, timeout=30)
        res.raise_for_status()
        text = res.json()["candidates"][0]["content"]["parts"][0]["text"]
        return json.loads(text)

    def _synthesize_with_groq(self, meeting_name: str, dialogue: List[Dict], screen_context: List[Dict] = None, template: str = "executive") -> Dict:
        url = "https://api.groq.com/openai/v1/chat/completions"
        prompt = self._format_prompt(meeting_name, dialogue, screen_context, template)
        headers = {
            "Authorization": f"Bearer {self.groq_api_key}",
            "Content-Type": "application/json"
        }
        payload = {
            "model": config.settings().get("groq_model", "llama-3.3-70b-versatile"),
            "messages": [
                {"role": "system", "content": "You are a professional executive meeting note extractor. Always output strict JSON."},
                {"role": "user", "content": prompt}
            ],
            "response_format": {"type": "json_object"},
            "temperature": 0.2
        }
        res = requests.post(url, headers=headers, json=payload, timeout=30)
        res.raise_for_status()
        content = res.json()["choices"][0]["message"]["content"]
        return json.loads(content)

    def _synthesize_local(self, meeting_name: str, dialogue: List[Dict], screen_context: List[Dict] = None, template: str = "executive") -> Dict:
        """Offline deterministic synthesizer requiring 0 cloud API tokens."""
        if not dialogue:
            tldr = "Brief audio recording with minimal speech detected."
            summary = "Session completed. No distinct conversational turns were transcribed."
            actions = []
            for_you = ["Verify audio input levels and microphone selection."]
            decisions = []
        else:
            text_corpus = " ".join([d.get("text", "") for d in dialogue])
            words = text_corpus.split()
            word_count = len(words)

            tldr = f"Discussion covering {word_count} words across {len(dialogue)} conversational turns."
            summary = f"Session recorded with {len(dialogue)} speaker segments. Topics covered primary operational discussion, workflow updates, and coordination."

            actions = []
            decisions = []
            for_you = []

            action_triggers = ["will", "need to", "action", "follow up", "send", "check", "create", "deploy", "review"]
            for d in dialogue:
                txt = d.get("text", "")
                speaker = d.get("speaker") or config.user_name()
                for trig in action_triggers:
                    if trig in txt.lower():
                        cleaned = re.sub(r'^(yeah|okay|so|and|well)\b\s*,?\s*', '', txt, flags=re.IGNORECASE)
                        if len(cleaned) > 15 and not any(a["task"] == cleaned for a in actions):
                            actions.append({
                                "owner": speaker,
                                "task": cleaned,
                                "status": "pending"
                            })
                            if speaker == config.user_name():
                                for_you.append(cleaned)
                        break

            if not actions:
                actions.append({
                    "owner": config.user_name(),
                    "task": "Review meeting notes and follow up on discussed points.",
                    "status": "pending"
                })
                for_you.append("Review session notes and timeline context.")

        topics = []
        if screen_context:
            unique_apps = list(dict.fromkeys([s.get("window_title", "") for s in screen_context if s.get("window_title")]))
            if unique_apps:
                topics.append({
                    "title": "On-Screen Context & Work Applications",
                    "summary": f"Active during call: {', '.join(unique_apps[:4])}"
                })

        return {
            "title": meeting_name,
            "template": template,
            "tldr": tldr,
            "summary": summary,
            "for_you": for_you[:5],
            "action_items": actions[:8],
            "decisions": decisions,
            "topics": topics
        }

    def render_markdown(self, summary_data: Dict, dialogue: List[Dict], screen_context: List[Dict] = None) -> str:
        """Renders markdown adhering cleanly to OpenBird note schema with template awareness."""
        title = summary_data.get("title", "Meeting")
        template = summary_data.get("template", "executive")
        tldr = summary_data.get("tldr", "")
        summary = summary_data.get("summary", "")
        for_you = summary_data.get("for_you", [])
        actions = summary_data.get("action_items", [])
        decisions = summary_data.get("decisions", [])
        topics = summary_data.get("topics", [])

        md = [f"# Meeting: {title}\n"]

        if template == "actions_only":
            md.append(f"> **Actions & Decisions Brief**: Focused task commitments.\n")
            md.append("## Action Items\n")
            for a in actions:
                owner = a.get("owner") or config.user_name()
                task = a.get("task", "")
                md.append(f"- [ ] **{owner}**: {task}")
            md.append("\n## Decisions Made\n")
            if decisions:
                for d in decisions:
                    md.append(f"- {d}")
            else:
                md.append("*No explicit decisions recorded.*")
            return "\n".join(md).strip()

        # Default & Technical schema
        md.append(f"> **TL;DR**: {tldr}\n")
        md.append("## Executive Summary\n")
        md.append(f"{summary}\n")

        if for_you:
            md.append("## For You\n")
            for item in for_you:
                md.append(f"- {item}")
            md.append("")

        if actions:
            md.append("## Action Items\n")
            for a in actions:
                owner = a.get("owner") or config.user_name()
                task = a.get("task", "")
                status = a.get("status", "pending")
                check = "[x]" if status == "completed" else "[ ]"
                md.append(f"- {check} **{owner}**: {task}")
            md.append("")

        if decisions:
            md.append("## Decisions\n")
            for d in decisions:
                md.append(f"- {d}")
            md.append("")

        if topics:
            md.append("## Key Discussion Topics\n")
            for t in topics:
                md.append(f"### {t.get('title', 'Topic')}")
                md.append(f"{t.get('summary', '')}\n")

        if screen_context:
            md.append("## Visual & Screen Context Timeline\n")
            for s in screen_context:
                time_str = s.get("time_str", "00:00")
                app = s.get("window_title", "Screen")
                ocr = s.get("ocr_text", "")
                md.append(f"### `[{time_str}]` {app}")
                if ocr:
                    snippet = ocr[:240].replace("\n", " ").strip()
                    md.append(f"> {snippet}\n")
            md.append("")

        if dialogue:
            md.append("## Full Transcript\n")
            for d in dialogue:
                time_str = d.get("timestamp", "00:00")
                speaker = d.get("speaker", "Speaker")
                text = d.get("text", "")
                md.append(f"**[{time_str}] {speaker}**: {text}\n")

        return "\n".join(md).strip()
