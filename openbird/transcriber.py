import os
import sys
import time
import gc
import logging
import sherpa_onnx
import soundfile as sf
import numpy as np

from . import config

logger = logging.getLogger("openbird.transcriber")

class Transcriber:
    def __init__(self, model_dir: str = None, num_threads: int = 4):
        self.model_dir = model_dir or config.default_path("whisper_model_dir")
        self.num_threads = num_threads
        self.recognizer = None
        self._init_recognizer()

    def _init_recognizer(self):
        encoder = os.path.join(self.model_dir, "base.en-encoder.int8.onnx")
        decoder = os.path.join(self.model_dir, "base.en-decoder.int8.onnx")
        tokens = os.path.join(self.model_dir, "base.en-tokens.txt")

        if not os.path.exists(encoder) or not os.path.exists(decoder):
            raise FileNotFoundError(f"Speech model not found in {self.model_dir}. Run: python main.py setup")

        logger.info(f"Initializing Sherpa-ONNX Whisper from {self.model_dir} (threads={self.num_threads})")
        t0 = time.time()
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_whisper(
            encoder=encoder,
            decoder=decoder,
            tokens=tokens,
            language="en",
            num_threads=self.num_threads,
            enable_segment_timestamps=True
        )
        logger.info(f"Loaded recognizer in {time.time() - t0:.2f}s")

    def _transcribe_mono_chunk(self, audio: np.ndarray, sample_rate: int) -> str:
        # Check if essentially silence
        rms = np.sqrt(np.mean(audio**2))
        if rms < 0.005:  # silence threshold
            return ""

        stream = self.recognizer.create_stream()
        try:
            stream.accept_waveform(sample_rate, audio)
            self.recognizer.decode_stream(stream)
            text = stream.result.text.strip()
            # Filter common hallucinated Whisper tokens during silence
            if text.lower() in ["[buzz]", "[silence]", "[music]", "(buzz)", "you", "thank you.", ""]:
                return ""
            return text
        finally:
            del stream

    def transcribe_dual_channel(self, wav_path: str, chunk_duration_sec: float = 10.0,
                                host_speaker: str = None, remote_speaker: str = "Remote Participants") -> list:
        """
        Transcribes a dual-channel WAV file with channel separation and custom speaker tags.
        Channel 0 = Host Mic (default: the signed-in user's first name)
        Channel 1 = System Loopback (Default: 'Remote Participants')
        Returns a chronologically sorted list of dialogue turns:
        [{"timestamp": "00:15", "start_sec": 15.0, "speaker": "Host", "channel": 0, "text": "..."}]
        """
        host_speaker = host_speaker or config.user_name()
        audio, sample_rate = sf.read(wav_path)
        if audio.ndim == 1:
            # Single channel fallback
            audio = np.column_stack((audio, np.zeros_like(audio)))

        total_samples = len(audio)
        chunk_samples = int(chunk_duration_sec * sample_rate)
        dialogue = []

        logger.info(f"Transcribing {wav_path} ({total_samples / sample_rate:.2f}s, {total_samples // chunk_samples + 1} chunks) [Speakers: {host_speaker} vs {remote_speaker}]...")

        try:
            for start_idx in range(0, total_samples, chunk_samples):
                end_idx = min(start_idx + chunk_samples, total_samples)
                start_sec = start_idx / sample_rate
                chunk = audio[start_idx:end_idx]

                ch0 = chunk[:, 0]
                ch1 = chunk[:, 1]

                rms0 = np.sqrt(np.mean(ch0**2))
                rms1 = np.sqrt(np.mean(ch1**2))

                # Transcribe active channel(s)
                min_rms = 0.008

                # Host track (Channel 0)
                if rms0 > min_rms and rms0 >= rms1 * 0.5:
                    text0 = self._transcribe_mono_chunk(ch0, sample_rate)
                    if text0:
                        mins = int(start_sec // 60)
                        secs = int(start_sec % 60)
                        dialogue.append({
                            "start_sec": start_sec,
                            "timestamp": f"{mins:02d}:{secs:02d}",
                            "speaker": host_speaker,
                            "channel": 0,
                            "text": text0
                        })

                # Remote participants track (Channel 1)
                if rms1 > min_rms and rms1 >= rms0 * 0.5:
                    text1 = self._transcribe_mono_chunk(ch1, sample_rate)
                    if text1:
                        mins = int(start_sec // 60)
                        secs = int(start_sec % 60)
                        dialogue.append({
                            "start_sec": start_sec,
                            "timestamp": f"{mins:02d}:{secs:02d}",
                            "speaker": remote_speaker,
                            "channel": 1,
                            "text": text1
                        })

            dialogue.sort(key=lambda x: x["start_sec"])
            return dialogue
        finally:
            # Explicit garbage collection to release ONNX tensors from RAM
            gc.collect()
