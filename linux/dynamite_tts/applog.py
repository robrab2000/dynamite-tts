"""Diagnostics log at ~/.local/state/dynamite-tts/dynamite.log. Timings and phases only, never the spoken text."""
from __future__ import annotations

import os
import sys
import threading
from datetime import datetime, timezone

from .settings import STATE_DIR

LOG_PATH = STATE_DIR / "dynamite.log"
_lock = threading.Lock()
_handle = None


def open_log() -> None:
    global _handle
    with _lock:
        if _handle is not None:
            return
        try:
            STATE_DIR.mkdir(parents=True, exist_ok=True)
            _handle = open(LOG_PATH, "a", encoding="utf-8")
        except OSError:
            _handle = None
    write(f"launch pid={os.getpid()} python={sys.version.split()[0]} session={os.environ.get('XDG_SESSION_TYPE', '?')}")


def write(message: str) -> None:
    stamp = datetime.now(timezone.utc).isoformat(timespec="seconds")
    line = f"{stamp} {message}"
    print(line, flush=True)
    with _lock:
        if _handle is None:
            return
        try:
            _handle.write(line + "\n")
            _handle.flush()
        except OSError:
            pass
