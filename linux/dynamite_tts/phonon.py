"""In-process Phonon speech through Gradium's `ptts` package (github.com/gradium-ai/xn-ptts).

Phonon is a 31M-parameter flow-matching model that streams 80 ms frames as it generates them,
so the first audio arrives within tens of milliseconds and a paragraph renders about 10x faster
than realtime on a few CPU cores. It needs no server: `linux/install-phonon.sh` puts the `ptts`
wheel in a private venv under the data directory and this module imports it from there.

The engine is loaded lazily on a worker thread and kept for the life of the process. Audio is
converted to the 16-bit 24 kHz mono PCM that `playback.PCMPlayer` expects.
"""
from __future__ import annotations

import glob
import math
import os
import sys
import threading
from pathlib import Path

from . import applog
from .settings import DATA_DIR

SAMPLE_RATE = 24_000
VENV_DIR = DATA_DIR / "phonon-venv"
DEFAULT_VOICE = "default"
#: Largest |duration_delta| the model speaks reliably; about 0.67x-1.5x of its natural rate.
MAX_DURATION_DELTA = 0.8


class PhononError(Exception):
    pass


def duration_delta_for_speed(speed: float) -> float:
    """Phonon's `duration_delta` condition for a speed multiplier (negative = faster).

    Measured on phonon-31m-en: -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x, i.e. speed ≈ exp(-delta/2).
    """
    try:
        speed = float(speed)
    except (TypeError, ValueError):
        return 0.0
    if not math.isfinite(speed) or speed <= 0:
        return 0.0
    return max(-MAX_DURATION_DELTA, min(MAX_DURATION_DELTA, -2.0 * math.log(speed)))


def pcm16_bytes(samples) -> bytes:
    """float32 samples in [-1, 1] (a NumPy array or any sequence) as little-endian 16-bit PCM."""
    try:
        import numpy as np  # ptts depends on numpy, so it is there whenever the engine is

        arr = np.asarray(samples, dtype=np.float32)
        return (np.clip(arr, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
    except ImportError:
        import struct

        values = [max(-32768, min(32767, int(max(-1.0, min(1.0, float(s))) * 32767))) for s in samples]
        return struct.pack(f"<{len(values)}h", *values)


def venv_site_packages() -> Path | None:
    for candidate in sorted(glob.glob(str(VENV_DIR / "lib" / "python3*" / "site-packages"))):
        if os.path.isdir(candidate):
            return Path(candidate)
    return None


def import_ptts():
    """`ptts` from the private venv (preferred) or the running interpreter."""
    site = venv_site_packages()
    if site is not None and str(site) not in sys.path:
        sys.path.append(str(site))
    try:
        import ptts  # type: ignore
    except ImportError as error:
        raise PhononError("Phonon is not installed. Run linux/install-phonon.sh, then try again.") from error
    return ptts


def is_installed() -> bool:
    site = venv_site_packages()
    if site is not None and (site / "ptts").is_dir():
        return True
    try:
        import importlib.util

        return importlib.util.find_spec("ptts") is not None
    except (ImportError, ValueError):
        return False


def default_threads() -> int:
    """A few threads serve one stream of speech best: measured 3-4 threads ≈ 10x realtime, all cores ≈ 5x."""
    return max(2, min(4, (os.cpu_count() or 4) // 4))


def quant_for(model_dir: str | None) -> str | None:
    if not model_dir:
        return "q8"
    return "q8" if (Path(model_dir) / "model.q8.gguf").is_file() else None


class PhononEngine:
    """One loaded `ptts.TTS`, reloaded only when the model folder or language changes."""

    def __init__(self):
        self._lock = threading.Lock()
        self._tts = None
        self._model_dir: str | None = None
        self._lang: str | None = None
        self._delta: float | None = None

    @property
    def is_loaded(self) -> bool:
        return self._tts is not None

    def load(self, model_dir: str | None, lang: str):
        model_dir = (model_dir or "").strip() or None
        lang = (lang or "en").strip().lower() or "en"
        with self._lock:
            if self._tts is not None and self._model_dir == model_dir and self._lang == lang:
                return self._tts
            ptts = import_ptts()
            if self._tts is None:
                try:
                    ptts.set_num_threads(default_threads())
                except Exception:  # noqa: BLE001 - older wheels may lack it
                    pass
            applog.write(f"phonon loading model={model_dir or 'default'} lang={lang}")
            try:
                tts = ptts.TTS(model=model_dir, lang=lang, quant=quant_for(model_dir))
            except Exception as error:  # noqa: BLE001 - surfaced to the user as a notice
                raise PhononError(f"Phonon could not load: {error}") from error
            self._tts, self._model_dir, self._lang, self._delta = tts, model_dir, lang, None
            applog.write("phonon ready")
            return tts

    def set_speed(self, speed: float) -> None:
        delta = duration_delta_for_speed(speed)
        with self._lock:
            if self._tts is None or (self._delta is not None and abs(self._delta - delta) < 1e-4):
                return
            try:
                self._tts.set_conditions({"duration_delta": delta})
                self._delta = delta
            except Exception as error:  # noqa: BLE001
                applog.write(f"phonon speed {speed} rejected: {error}")


ENGINE = PhononEngine()


class PhononStream:
    """Speaks one text on a worker thread, handing 16-bit PCM to `on_pcm`; same shape as lemonade.PCMStream."""

    def __init__(self, on_pcm, on_complete, engine: PhononEngine = ENGINE):
        self._on_pcm = on_pcm
        self._on_complete = on_complete
        self._engine = engine
        self._cancelled = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self, text: str, voice: str, speed: float, lang: str, model_dir: str | None) -> None:
        self._thread = threading.Thread(
            target=self._run, args=(text, voice, speed, lang, model_dir), name="phonon-stream", daemon=True
        )
        self._thread.start()

    def cancel(self) -> None:
        self._cancelled.set()

    def _run(self, text: str, voice: str, speed: float, lang: str, model_dir: str | None) -> None:
        try:
            tts = self._engine.load(model_dir, lang)
            self._engine.set_speed(speed)
            kwargs = {}
            if voice and voice != DEFAULT_VOICE:
                kwargs["voice"] = voice
            try:
                stream = tts.stream(text, **kwargs)
            except Exception as error:  # noqa: BLE001
                if kwargs:
                    applog.write(f"phonon voice {voice!r} failed ({error}); using the default voice")
                    stream = tts.stream(text)
                else:
                    raise
            with stream as audio:
                for pcm in audio:
                    if self._cancelled.is_set():
                        break
                    data = pcm16_bytes(pcm)
                    if data:
                        self._on_pcm(data)
        except PhononError as error:
            if not self._cancelled.is_set():
                self._on_complete(error)
                return
        except Exception as error:  # noqa: BLE001 - keep the app alive and report
            if not self._cancelled.is_set():
                applog.write(f"phonon stream failed: {error!r}")
                self._on_complete(PhononError(f"Phonon failed: {error}"))
                return
        self._on_complete(None)
