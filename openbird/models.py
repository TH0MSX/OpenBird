"""
OpenBird - Speech model setup.

Downloads the offline Whisper model (sherpa-onnx, base.en, int8) on demand, so
nothing has to be placed by hand. Run:  python main.py setup
"""

import os
import shutil
import tarfile
import logging
import tempfile
import urllib.request
from typing import Callable, Optional

from . import config

logger = logging.getLogger("openbird.models")

MODEL_URL = ("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/"
             f"{config.WHISPER_MODEL_NAME}.tar.bz2")
REQUIRED_FILES = ["base.en-encoder.int8.onnx", "base.en-decoder.int8.onnx", "base.en-tokens.txt"]


def missing_model_files(model_dir: str) -> list:
    return [f for f in REQUIRED_FILES if not os.path.isfile(os.path.join(model_dir, f))]


def is_model_installed(model_dir: str = None) -> bool:
    return not missing_model_files(model_dir or config.default_path("whisper_model_dir"))


def install_model(model_dir: str = None, progress: Optional[Callable[[int, int], None]] = None,
                  url: str = MODEL_URL) -> str:
    """Downloads and unpacks only the three files the transcriber needs (about 200 MB download)."""
    model_dir = model_dir or config.default_path("whisper_model_dir")
    if is_model_installed(model_dir):
        return model_dir

    os.makedirs(model_dir, exist_ok=True)
    tmp_dir = tempfile.mkdtemp(prefix="openbird_model_")
    archive = os.path.join(tmp_dir, "model.tar.bz2")
    try:
        logger.info(f"Downloading speech model from {url}")
        req = urllib.request.Request(url, headers={"User-Agent": f"OpenBird/{config.APP_VERSION}"})
        with urllib.request.urlopen(req, timeout=60) as resp, open(archive, "wb") as out:
            total = int(resp.headers.get("Content-Length") or 0)
            done = 0
            while True:
                chunk = resp.read(1024 * 1024)
                if not chunk:
                    break
                out.write(chunk)
                done += len(chunk)
                if progress:
                    progress(done, total)
        if total and done != total:
            raise IOError(f"Download incomplete: {done} of {total} bytes.")

        with tarfile.open(archive, "r:bz2") as tar:
            for member in tar:
                name = os.path.basename(member.name)
                if member.isfile() and name in REQUIRED_FILES:
                    src = tar.extractfile(member)
                    part = os.path.join(model_dir, name + ".part")
                    with open(part, "wb") as dst:
                        shutil.copyfileobj(src, dst)
                    os.replace(part, os.path.join(model_dir, name))

        missing = missing_model_files(model_dir)
        if missing:
            raise IOError(f"Model archive did not contain: {', '.join(missing)}")
        logger.info(f"Speech model installed in {model_dir}")
        return model_dir
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)
