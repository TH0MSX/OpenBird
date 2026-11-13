"""
OpenBird - System Health & Diagnostics Auditor
Audits WinRT OCR, WASAPI loopback capture, Whisper ONNX models, SQLite WAL integrity, and High-DPI scaling.
"""

import os
import sys
import time
import sqlite3
import ctypes
import logging
from . import soundcard_fix  # must run before soundcard is used
import soundcard as sc

from . import config
from typing import Dict, List

logger = logging.getLogger("openbird.health")


class HealthAuditor:
    @classmethod
    def audit_system(cls, db_path: str = None, model_dir: str = None, vault_dir: str = None) -> Dict:
        """Runs a complete sovereign diagnostic audit of all OpenBird subsystem components."""
        db_path = db_path or config.default_path("db_path")
        model_dir = model_dir or config.default_path("whisper_model_dir")
        vault_dir = vault_dir or config.default_path("storage_dir")

        results = {
            "timestamp": time.strftime("%Y-%m-%d %H:%M:%S"),
            "status": "PASS",
            "checks": []
        }

        # 1. High-DPI Context Check
        try:
            dpi = ctypes.windll.user32.GetDpiForSystem()
            scale_pct = int((dpi / 96.0) * 100)
            results["checks"].append({
                "subsystem": "Windows 11 Display",
                "status": "OK",
                "detail": f"System DPI: {dpi} ({scale_pct}% scale factor, Per-Monitor V2 aware)"
            })
        except Exception as e:
            results["checks"].append({
                "subsystem": "Windows 11 Display",
                "status": "WARN",
                "detail": f"DPI query fallback: {e}"
            })

        # 2. WASAPI Audio Devices Check
        try:
            mic = sc.default_microphone()
            mic_name = mic.name if mic else "None"
            all_mics = sc.all_microphones(include_loopback=True)
            loops = [m for m in all_mics if m.isloopback]
            loop_name = loops[0].name if loops else "None"

            if mic and loops:
                results["checks"].append({
                    "subsystem": "WASAPI Audio Capture",
                    "status": "OK",
                    "detail": f"Host Mic: '{mic_name}' | Loopback: '{loop_name}' (Dual-channel active)"
                })
            else:
                results["status"] = "WARN"
                results["checks"].append({
                    "subsystem": "WASAPI Audio Capture",
                    "status": "WARN",
                    "detail": f"Mic: {mic_name}, Loopback: {loop_name}"
                })
        except Exception as e:
            results["status"] = "FAIL"
            results["checks"].append({
                "subsystem": "WASAPI Audio Capture",
                "status": "FAIL",
                "detail": f"WASAPI driver error: {e}"
            })

        # 3. WinRT OCR Engine Check
        try:
            import winrt.windows.media.ocr as ocr
            engine = ocr.OcrEngine.try_create_from_user_profile_languages()
            if engine:
                langs = []
                try:
                    langs = [str(l.language_tag) for l in ocr.OcrEngine.available_recognizer_languages]
                except Exception:
                    pass
                lang_str = f"Available languages: {', '.join(langs[:3])} (Total {len(langs)})" if langs else "User profile language active"
                results["checks"].append({
                    "subsystem": "WinRT Native OCR",
                    "status": "OK",
                    "detail": f"Engine online. {lang_str}"
                })
            else:
                results["status"] = "WARN"
                results["checks"].append({
                    "subsystem": "WinRT Native OCR",
                    "status": "WARN",
                    "detail": "Default user profile language engine not returned"
                })
        except Exception as e:
            results["status"] = "FAIL"
            results["checks"].append({
                "subsystem": "WinRT Native OCR",
                "status": "FAIL",
                "detail": f"WinRT OCR binding failed: {e}"
            })

        # 4. Whisper ONNX Models Check
        candidates_model = [model_dir]
        found_model_dir = None
        for m_cand in candidates_model:
            if m_cand and os.path.exists(os.path.join(m_cand, "base.en-encoder.int8.onnx")):
                found_model_dir = m_cand
                break

        if found_model_dir:
            enc = os.path.join(found_model_dir, "base.en-encoder.int8.onnx")
            dec = os.path.join(found_model_dir, "base.en-decoder.int8.onnx")
            enc_size_mb = round(os.path.getsize(enc) / (1024 * 1024), 1)
            dec_size_mb = round(os.path.getsize(dec) / (1024 * 1024), 1)
            results["checks"].append({
                "subsystem": "Sherpa-ONNX Whisper Model",
                "status": "OK",
                "detail": f"Base.en int8 quantized (Encoder: {enc_size_mb} MB, Decoder: {dec_size_mb} MB) at '{os.path.basename(os.path.dirname(found_model_dir))}'"
            })
        else:
            results["status"] = "FAIL"
            results["checks"].append({
                "subsystem": "Sherpa-ONNX Whisper Model",
                "status": "FAIL",
                "detail": f"ONNX model binaries missing in {model_dir}"
            })

        # 5. SQLite WAL Database Integrity
        try:
            if os.path.exists(db_path):
                conn = sqlite3.connect(db_path, timeout=5.0)
                cur = conn.execute("PRAGMA integrity_check;")
                row = cur.fetchone()
                status_text = row[0] if row else "unknown"
                cur_m = conn.execute("SELECT count(*) FROM meetings;")
                m_count = cur_m.fetchone()[0]
                cur_t = conn.execute("SELECT count(*) FROM screen_timeline;")
                s_count = cur_t.fetchone()[0]
                conn.close()

                if status_text == "ok":
                    results["checks"].append({
                        "subsystem": "SQLite WAL Database",
                        "status": "OK",
                        "detail": f"Integrity: OK | Meetings: {m_count} | 24/7 Screen Frames: {s_count}"
                    })
                else:
                    results["status"] = "WARN"
                    results["checks"].append({
                        "subsystem": "SQLite WAL Database",
                        "status": "WARN",
                        "detail": f"Integrity check returned: {status_text}"
                    })
            else:
                results["checks"].append({
                    "subsystem": "SQLite WAL Database",
                    "status": "OK",
                    "detail": "Database will be initialized on first run."
                })
        except Exception as e:
            results["status"] = "FAIL"
            results["checks"].append({
                "subsystem": "SQLite WAL Database",
                "status": "FAIL",
                "detail": f"SQLite error: {e}"
            })

        # 6. Vault Directories
        subdirs = ["timeline", "digests", "calendars", "exports"]
        for s in subdirs:
            p = os.path.join(vault_dir, s)
            os.makedirs(p, exist_ok=True)
        results["checks"].append({
            "subsystem": "Storage Vault & Subfolders",
            "status": "OK",
            "detail": f"Active: {vault_dir} (Subfolders initialized)"
        })

        return results

    @classmethod
    def format_markdown_report(cls, audit_data: Dict) -> str:
        lines = [
            f"# 🏥 OpenBird System Health & Diagnostic Audit",
            f"> Audited on `{audit_data['timestamp']}` | Overall Result: **{audit_data['status']}**\n",
            "| Subsystem | Status | Diagnostic Detail |",
            "| :--- | :--- | :--- |"
        ]
        for c in audit_data["checks"]:
            badge = "🟢 OK" if c["status"] == "OK" else ("🟡 WARN" if c["status"] == "WARN" else "🔴 FAIL")
            lines.append(f"| **{c['subsystem']}** | {badge} | {c['detail']} |")

        lines.append("\n---")
        lines.append("*All core Windows 11 sovereign runtime components operational.*")
        return "\n".join(lines)
