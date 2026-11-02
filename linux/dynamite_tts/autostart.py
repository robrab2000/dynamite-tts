"""Launch at login through an XDG autostart entry (~/.config/autostart/dynamite-tts.desktop)."""
from __future__ import annotations

import os
from pathlib import Path

from .hotkeys import launcher_command


def _autostart_path() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME")
    root = Path(base) if base else Path.home() / ".config"
    return root / "autostart" / "dynamite-tts.desktop"


def is_enabled() -> bool:
    return _autostart_path().exists()


def apply(enabled: bool) -> None:
    path = _autostart_path()
    if not enabled:
        try:
            path.unlink()
        except FileNotFoundError:
            pass
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "[Desktop Entry]\n"
        "Type=Application\n"
        "Name=Dynamite TTS\n"
        "Comment=Highlight-to-speech\n"
        f"Exec={launcher_command()}\n"
        "Icon=dynamite-tts\n"
        "Terminal=false\n"
        "X-GNOME-Autostart-enabled=true\n"
        "X-GNOME-Autostart-Delay=5\n"
    )
