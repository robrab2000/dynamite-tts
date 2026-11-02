"""Settings model and JSON store (~/.config/dynamite-tts/settings.json)."""
from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field, fields
from enum import Enum
from pathlib import Path


class SpeakMode(str, Enum):
    VERBATIM = "verbatim"
    SUMMARY = "summary"
    EXPLAIN = "explain"

    @property
    def title(self) -> str:
        return self.value.capitalize()

    def next(self) -> "SpeakMode":
        order = [SpeakMode.VERBATIM, SpeakMode.SUMMARY, SpeakMode.EXPLAIN]
        return order[(order.index(self) + 1) % len(order)]


class ExplainAudience(str, Enum):
    TECHNICAL = "technical"
    GENERAL = "general"

    @property
    def title(self) -> str:
        return self.value.capitalize()


# Hotkeys are stored as GTK accelerator strings ("<Control><Shift>s"). GNOME's
# custom keybindings use the same syntax, so they can be written straight into gsettings.
DEFAULT_SPEAK_HOTKEY = "<Control><Shift>s"
DEFAULT_MODE_HOTKEY = "<Control><Shift>m"


@dataclass
class AppSettings:
    base_url: str = "http://127.0.0.1:13305"
    voice: str = "af_heart"
    speed: float = 1.0
    speak_mode: SpeakMode = SpeakMode.VERBATIM
    speak_hotkey: str = DEFAULT_SPEAK_HOTKEY
    mode_hotkey: str = DEFAULT_MODE_HOTKEY
    audio_device: str = ""
    launch_at_login: bool = False
    show_status_notifications: bool = True
    play_sound_on_stop: bool = True
    explain_sentence_count: int = 3
    explain_audience: ExplainAudience = ExplainAudience.TECHNICAL
    chat_model: str = "Llama-3.2-1B-Instruct-GGUF"

    @property
    def clamped_explain_sentences(self) -> int:
        return min(8, max(1, int(self.explain_sentence_count)))

    def normalized(self) -> "AppSettings":
        self.explain_sentence_count = self.clamped_explain_sentences
        self.speed = min(4.0, max(0.25, float(self.speed)))
        self.base_url = self.base_url.strip().rstrip("/") or "http://127.0.0.1:13305"
        if not self.speak_hotkey.strip():
            self.speak_hotkey = DEFAULT_SPEAK_HOTKEY
        if not self.mode_hotkey.strip():
            self.mode_hotkey = DEFAULT_MODE_HOTKEY
        return self

    def to_json(self) -> dict:
        data = asdict(self)
        data["speak_mode"] = self.speak_mode.value
        data["explain_audience"] = self.explain_audience.value
        return data

    @classmethod
    def from_json(cls, data: dict) -> "AppSettings":
        settings = cls()
        known = {f.name for f in fields(cls)}
        for key, value in data.items():
            if key not in known:
                continue
            try:
                if key == "speak_mode":
                    value = SpeakMode(value)
                elif key == "explain_audience":
                    value = ExplainAudience(value)
                setattr(settings, key, value)
            except (ValueError, TypeError):
                continue
        return settings.normalized()


VOICES: list[tuple[str, str]] = [
    ("af_heart", "af_heart · US female, warm"),
    ("af_sky", "af_sky · US female, clear"),
    ("af_bella", "af_bella · US female, expressive"),
    ("af_sarah", "af_sarah · US female, smooth"),
    ("af_nicole", "af_nicole · US female, crisp"),
    ("am_adam", "am_adam · US male, narrative"),
    ("am_michael", "am_michael · US male, professional"),
    ("am_echo", "am_echo · US male, balanced"),
    ("bf_emma", "bf_emma · UK female"),
    ("bf_alice", "bf_alice · UK female, articulate"),
    ("bm_george", "bm_george · UK male"),
    ("bm_daniel", "bm_daniel · UK male, conversational"),
    ("coral", "coral · OpenAI alias"),
]


def _xdg(var: str, fallback: str) -> Path:
    value = os.environ.get(var)
    return Path(value) if value else Path.home() / fallback


CONFIG_DIR = _xdg("XDG_CONFIG_HOME", ".config") / "dynamite-tts"
DATA_DIR = _xdg("XDG_DATA_HOME", ".local/share") / "dynamite-tts"
STATE_DIR = _xdg("XDG_STATE_HOME", ".local/state") / "dynamite-tts"


def runtime_dir() -> Path:
    value = os.environ.get("XDG_RUNTIME_DIR")
    if value and os.path.isdir(value):
        return Path(value)
    return Path(f"/tmp/dynamite-tts-{os.getuid()}")


class SettingsStore:
    def __init__(self, path: Path | None = None):
        self.path = path or CONFIG_DIR / "settings.json"
        self.current = self._load()

    def update(self, mutate) -> None:
        mutate(self.current)
        self.current.normalized()
        self.save()

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(self.current.to_json(), indent=2, sort_keys=True) + "\n")
        os.replace(tmp, self.path)

    def _load(self) -> AppSettings:
        try:
            return AppSettings.from_json(json.loads(self.path.read_text()))
        except (OSError, ValueError):
            return AppSettings()
