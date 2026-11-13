"""
OpenBird - Unified WASAPI Audio Engine with Dual-Channel Audio & Feedback Chimes
Zero Cloud Dependencies, Pre-Roll Ring Buffer, Keyboard/Fan Noise Filtering, and Graceful Device Teardown.
"""

import os
import sys
import time
import queue
import logging
import threading
import warnings
from typing import Dict, List, Optional, Tuple

import numpy as np
from . import soundcard_fix  # must run before soundcard is used
import soundcard as sc
import soundfile as sf

from . import config

try:
    import winsound
    WINSOUND_AVAILABLE = True
except ImportError:
    WINSOUND_AVAILABLE = False

if sys.stdout is not None:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

warnings.filterwarnings("ignore", category=sc.SoundcardRuntimeWarning)
logger = logging.getLogger("openbird.audio")

# Serialises every soundcard device lookup and stream open/close.
_device_enum_lock = threading.Lock()


def _play_tone_async(freq1: int, dur1: int, freq2: int, dur2: int):
    """Plays non-blocking melodic sound chimes for instant tactile audio feedback."""
    def worker():
        if not WINSOUND_AVAILABLE:
            return
        try:
            winsound.Beep(freq1, dur1)
            time.sleep(0.04)
            winsound.Beep(freq2, dur2)
        except Exception:
            pass
    threading.Thread(target=worker, daemon=True).start()


def play_start_chime():
    """Ascending chime (880Hz -> 1320Hz): Session started."""
    _play_tone_async(880, 90, 1320, 130)


def play_stop_chime():
    """Descending chime (1320Hz -> 880Hz): Session stopped / synthesizing."""
    _play_tone_async(1320, 90, 880, 130)


def is_speech_block(block: np.ndarray, rms: float, threshold: float) -> bool:
    """Filters mechanical keyboard clicks and fan hum using crest factor and zero-crossing rate."""
    if rms < threshold:
        return False
    # Crest factor: peak / rms (impulsive keystrokes spike sharply > 6.5)
    peak = float(np.max(np.abs(block)))
    crest = peak / (rms + 1e-6)
    if crest > 6.8 and rms < threshold * 2.2:
        return False  # Isolated keyboard click or mouse click

    # Zero-crossing rate: continuous fan hiss has high ZCR (> 0.42)
    flat = block.flatten()
    if len(flat) > 1:
        zcr = float(np.mean(np.abs(np.diff(np.sign(flat)))) / 2.0)
        if zcr > 0.42:
            return False  # High frequency hiss
    return True


class AudioRecorder:
    def __init__(self, sample_rate: int = 16000, output_dir: str = None,
                 speech_threshold: float = 0.015, min_speech_sec: float = 1.5, silence_timeout_sec: float = 25.0):
        self.sample_rate = sample_rate
        self.output_dir = output_dir or config.default_path("recordings_dir")
        self.speech_threshold = speech_threshold
        self.min_speech_sec = min_speech_sec
        self.silence_timeout_sec = silence_timeout_sec

        os.makedirs(self.output_dir, exist_ok=True)

        self.is_running = False
        self.is_recording = False
        self.auto_vad_enabled = True

        self._stop_event = threading.Event()
        self._threads = []
        self._state_lock = threading.Lock()

        self.mic_queue = queue.Queue(maxsize=100)
        self.loop_queue = queue.Queue(maxsize=100)

        # Rolling buffers (last 3 seconds of pre-speech audio)
        self.rolling_mic = []
        self.rolling_loop = []
        self.meeting_mic = []
        self.meeting_loop = []

        self.current_title = "Meeting"
        self.current_meeting_id = None
        self.meeting_start_time = 0.0
        self.last_speech_time = time.time()
        self.consecutive_speech_time = 0.0
        self.current_mic_rms = 0.0
        self.current_loop_rms = 0.0
        self.is_mic_muted = False
        self._recording_blocks_count = 0
        self._recording_mic_total_rms = 0.0
        self._recording_loop_total_rms = 0.0

        # Callbacks
        self.on_meeting_started = None
        self.on_meeting_finished = None
        self.on_mic_muted_warning = None

    @classmethod
    def get_device_diagnostics(cls) -> Dict:
        """Inspects all detected hardware audio capture devices for diagnostics and testing."""
        try:
            mics = [m.name for m in sc.all_microphones(include_loopback=False)]
            loopbacks = [m.name for m in sc.all_microphones(include_loopback=True) if m.isloopback]
            default_mic = sc.default_microphone().name
            default_speaker = sc.default_speaker().name
            return {
                "microphones": mics,
                "loopbacks": loopbacks,
                "default_mic": default_mic,
                "default_speaker": default_speaker,
                "has_mic": len(mics) > 0,
                "has_loopback": len(loopbacks) > 0
            }
        except Exception as e:
            logger.debug(f"Device diagnostics note: {e}")
            return {
                "microphones": ["Default Windows Microphone"],
                "loopbacks": ["Default Windows Loopback"],
                "default_mic": "Default Windows Microphone",
                "default_speaker": "Default Windows Speaker",
                "has_mic": True,
                "has_loopback": True
            }

    def start_engine(self):
        if self.is_running:
            return
        self.is_running = True
        self._stop_event.clear()

        mic, loop = self._get_devices()
        logger.info(f"Audio capture devices: Mic='{mic.name if mic else 'None'}', Loopback='{loop.name if loop else 'None'}'")

        block_size = 2048

        # Devices are looked up HERE, on the calling thread, and handed to the readers.
        # Looking devices up inside the reader threads (the v2.4 "sleep recovery" change)
        # corrupts the heap in soundcard and kills the process within a second.
        t_mic = threading.Thread(target=self._reader_worker, args=(mic, "Microphone", self.mic_queue, block_size), daemon=True) if mic else None
        t_loop = threading.Thread(target=self._reader_worker, args=(loop, "Loopback", self.loop_queue, block_size), daemon=True) if loop else None
        t_proc = threading.Thread(target=self._processor_worker, args=(block_size,), daemon=True)
        if not mic:
            logger.warning("No microphone found: your side of meetings will not be recorded.")
        if not loop:
            logger.warning("No loopback device found: system audio will not be recorded.")

        self._threads = [t for t in (t_mic, t_loop, t_proc) if t is not None]
        for t in self._threads:
            t.start()

        logger.info("Unified Audio Engine started.")

    def stop_engine(self):
        """Gracefully halts WASAPI streams and drains capture queues without deadlock."""
        self.is_running = False
        self._stop_event.set()

        while not self.mic_queue.empty():
            try:
                self.mic_queue.get_nowait()
            except Exception:
                break

        while not self.loop_queue.empty():
            try:
                self.loop_queue.get_nowait()
            except Exception:
                break

        for t in self._threads:
            try:
                t.join(timeout=1.0)
            except Exception:
                pass
        self._threads.clear()
        logger.info("Unified Audio Engine cleanly stopped.")

    def _get_devices(self):
        with _device_enum_lock:
            try:
                mic = sc.default_microphone()
            except Exception as e:
                logger.warning(f"Default microphone lookup failed: {e}")
                mic = None
            try:
                loopbacks = [m for m in sc.all_microphones(include_loopback=True) if m.isloopback]
                loop = loopbacks[0] if loopbacks else None
            except Exception as e:
                logger.warning(f"Loopback device lookup failed: {e}")
                loop = None
        return mic, loop

    def _reader_worker(self, device, dev_type: str, out_queue: queue.Queue, block_size: int):
        retry_delay = 0.5
        failures = 0
        while not self._stop_event.is_set():
            rec_cm = None
            try:
                # Open one stream at a time; the two readers must not create recorders simultaneously.
                with _device_enum_lock:
                    rec_cm = device.recorder(samplerate=self.sample_rate, channels=1, blocksize=block_size)
                    rec = rec_cm.__enter__()

                if failures:
                    logger.info(f"{dev_type} stream recovered after {failures} failed attempt(s).")
                else:
                    logger.info(f"{dev_type} stream open.")
                retry_delay = 0.5
                failures = 0
                while not self._stop_event.is_set():
                    data = rec.record(numframes=block_size)
                    if len(data) > 0:
                        try:
                            out_queue.put(data, timeout=0.1)
                        except queue.Full:
                            try:
                                out_queue.get_nowait()
                                out_queue.put_nowait(data)
                            except Exception:
                                pass
            except Exception as e:
                if not self._stop_event.is_set():
                    failures += 1
                    # First failure and every 20th are visible in the log; the rest stay quiet.
                    if failures == 1 or failures % 20 == 0:
                        logger.warning(f"{dev_type} stream error (attempt {failures}), reopening: {e}")
                    self._stop_event.wait(retry_delay)
                    retry_delay = min(5.0, retry_delay * 1.5)
            finally:
                if rec_cm is not None:
                    with _device_enum_lock:
                        try:
                            rec_cm.__exit__(None, None, None)
                        except Exception as e:
                            logger.warning(f"{dev_type} stream close error: {e}")

    def _processor_worker(self, block_size):
        block_sec = block_size / self.sample_rate
        max_rolling_blocks = int(3.0 / block_sec)
        zero_block = np.zeros((block_size, 1), dtype=np.float32)

        while not self._stop_event.is_set():
            try:
                mic_data = self.mic_queue.get(timeout=0.2)
            except queue.Empty:
                continue

            try:
                loop_data = self.loop_queue.get_nowait()
            except queue.Empty:
                loop_data = zero_block

            mic_rms = float(np.sqrt(np.mean(mic_data**2)))
            loop_rms = float(np.sqrt(np.mean(loop_data**2))) if len(loop_data) > 0 else 0.0
            self.current_mic_rms = mic_rms
            self.current_loop_rms = loop_rms

            # Acoustically verified speech check
            mic_has_speech = is_speech_block(mic_data, mic_rms, self.speech_threshold)
            loop_has_speech = (loop_rms >= self.speech_threshold)
            has_speech = mic_has_speech or loop_has_speech

            with self._state_lock:
                if not self.is_recording:
                    # Maintain 3-second rolling pre-buffer
                    self.rolling_mic.append(mic_data)
                    self.rolling_loop.append(loop_data)
                    if len(self.rolling_mic) > max_rolling_blocks:
                        self.rolling_mic.pop(0)
                    if len(self.rolling_loop) > max_rolling_blocks:
                        self.rolling_loop.pop(0)

                    if self.auto_vad_enabled:
                        if has_speech:
                            self.consecutive_speech_time += block_sec
                            if self.consecutive_speech_time >= self.min_speech_sec:
                                logger.info(f"VAD detected sustained speech ({self.consecutive_speech_time:.1f}s). Auto-starting session...")
                                self._internal_start_recording(title="Spoken Conversation")
                        else:
                            self.consecutive_speech_time = max(0.0, self.consecutive_speech_time - block_sec)

                else:
                    # Actively recording meeting
                    self.meeting_mic.append(mic_data)
                    self.meeting_loop.append(loop_data)

                    # Zero-energy / hardware muted mic detection check
                    self._recording_blocks_count += 1
                    self._recording_mic_total_rms += mic_rms
                    self._recording_loop_total_rms += loop_rms

                    if self._recording_blocks_count == 24: # ~3 seconds into session
                        avg_mic = self._recording_mic_total_rms / 24.0
                        avg_loop = self._recording_loop_total_rms / 24.0
                        if avg_mic < 0.0001 and avg_loop > 0.003:
                            self.is_mic_muted = True
                            logger.warning("Microphone hardware mute detected (zero mic signal while loopback active).")
                            if self.on_mic_muted_warning:
                                try:
                                    self.on_mic_muted_warning()
                                except Exception:
                                    pass

                    if has_speech:
                        self.last_speech_time = time.time()
                    elif time.time() - self.last_speech_time >= self.silence_timeout_sec:
                        logger.info(f"Silence timeout ({self.silence_timeout_sec}s) reached. Auto-finalizing session...")
                        self._internal_stop_recording(is_auto=True)

    def _internal_start_recording(self, title: str = "Meeting") -> str:
        self.current_title = title
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        self.current_meeting_id = f"mtg_{timestamp}"
        self.meeting_start_time = time.time()

        # Seed with pre-speech rolling buffer
        self.meeting_mic = list(self.rolling_mic)
        self.meeting_loop = list(self.rolling_loop)
        self.last_speech_time = time.time()
        self.consecutive_speech_time = 0.0
        self.is_recording = True

        self.is_mic_muted = False
        self._recording_blocks_count = 0
        self._recording_mic_total_rms = 0.0
        self._recording_loop_total_rms = 0.0

        play_start_chime()

        if self.on_meeting_started:
            try:
                self.on_meeting_started(self.current_meeting_id, self.current_title)
            except Exception as e:
                logger.debug(f"on_meeting_started callback error: {e}")

        logger.info(f"Recording session started: '{title}' ({self.current_meeting_id})")
        return self.current_meeting_id

    def _internal_stop_recording(self, is_auto: bool = False) -> dict:
        if not self.is_recording:
            return {}

        self.is_recording = False
        duration = time.time() - self.meeting_start_time
        meeting_id = self.current_meeting_id
        title = self.current_title

        play_stop_chime()

        if not self.meeting_mic:
            logger.warning("Empty audio collected for meeting.")
            return {}

        mic_array = np.concatenate(self.meeting_mic, axis=0)
        loop_array = np.concatenate(self.meeting_loop, axis=0) if self.meeting_loop else np.zeros_like(mic_array)

        # Equalize lengths
        min_len = min(len(mic_array), len(loop_array))
        stereo_data = np.zeros((min_len, 2), dtype=np.float32)
        stereo_data[:, 0] = mic_array[:min_len, 0]   # Channel 0: host microphone
        stereo_data[:, 1] = loop_array[:min_len, 0]  # Channel 1: Remote Speakers (Loopback)

        wav_path = os.path.join(self.output_dir, f"{meeting_id}.wav")
        sf.write(wav_path, stereo_data, self.sample_rate)
        logger.info(f"Saved recording: {wav_path} ({duration:.2f}s)")

        self.meeting_mic.clear()
        self.meeting_loop.clear()

        meeting_data = {
            "meeting_id": meeting_id,
            "title": title,
            "duration": duration,
            "wav_path": wav_path,
            "start_time": self.meeting_start_time,
            "is_auto": is_auto
        }

        if self.on_meeting_finished:
            t = threading.Thread(target=self.on_meeting_finished, args=(meeting_data,), daemon=True)
            t.start()

        return meeting_data

    def start(self, title: str = "Manual Meeting") -> str:
        with self._state_lock:
            return self._internal_start_recording(title=title)

    def stop(self) -> dict:
        with self._state_lock:
            return self._internal_stop_recording(is_auto=False)
