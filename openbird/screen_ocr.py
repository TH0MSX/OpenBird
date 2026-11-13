import os
import sys
import time
import asyncio
import logging
import ctypes
from typing import List, Dict, Optional
from PIL import ImageGrab
import winrt.windows.media.ocr as ocr
import winrt.windows.graphics.imaging as imaging
import winrt.windows.storage as storage

from . import config

logger = logging.getLogger("openbird.screen_ocr")

class ScreenContextTracker:
    def __init__(self, screenshots_dir: str = None):
        self.screenshots_dir = screenshots_dir or config.default_path("screenshots_dir")
        os.makedirs(self.screenshots_dir, exist_ok=True)
        self.user32 = ctypes.windll.user32
        self.history: List[Dict] = []
        self._is_tracking = False

    def get_active_window_title(self) -> str:
        try:
            hwnd = self.user32.GetForegroundWindow()
            if not hwnd:
                return "Unknown / Background"
            length = self.user32.GetWindowTextLengthW(hwnd)
            if length == 0:
                return "Desktop / Inactive"
            buff = ctypes.create_unicode_buffer(length + 1)
            self.user32.GetWindowTextW(hwnd, buff, length + 1)
            return buff.value.strip() or "Desktop / Inactive"
        except Exception:
            return "Unknown"

    async def _ocr_image_async(self, file_path: str) -> str:
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
            logger.debug(f"OCR processing failed for {file_path}: {e}")
            return ""

    def capture_frame(self, meeting_id: str = "general") -> Optional[Dict]:
        """Captures a screenshot, performs native OCR, and records the active window context."""
        window_title = self.get_active_window_title()
        timestamp_str = time.strftime("%Y%m%d_%H%M%S")
        img_name = f"frame_{meeting_id}_{timestamp_str}.png"
        img_path = os.path.join(self.screenshots_dir, img_name)

        ocr_text = ""
        try:
            if config.foreground_is_excluded():
                raise PermissionError("foreground window is on the capture exclusion list")
            screenshot = ImageGrab.grab(all_screens=bool(config.settings().get("capture_all_monitors", True)))
            screenshot.save(img_path, format="PNG")
            try:
                ocr_text = asyncio.run(self._ocr_image_async(img_path))
            except Exception as e:
                logger.debug(f"OCR invocation error: {e}")
        except Exception as e:
            img_path = None

        record = {
            "timestamp": time.time(),
            "time_str": time.strftime("%H:%M:%S"),
            "window_title": window_title,
            "image_path": img_path,
            "ocr_text": ocr_text
        }
        self.history.append(record)
        return record

    def get_context_for_period(self, start_time: float, end_time: float) -> List[Dict]:
        return [r for r in self.history if start_time <= r["timestamp"] <= end_time]
