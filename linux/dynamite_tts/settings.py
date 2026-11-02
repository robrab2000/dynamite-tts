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


class Engine(str, Enum):
    KOKORO = "kokoro"  # Lemonade's Kokoro over HTTP, installed the first time it is used
    PHONON = "phonon"  # Gradium's Phonon in-process (linux/install-phonon.sh); the default

    @property
    def title(self) -> str:
        return {Engine.KOKORO: "Kokoro (via Lemonade)", Engine.PHONON: "Phonon (in-process)"}[self]


@dataclass
class AppSettings:
    base_url: str = "http://127.0.0.1:13305"
    engine: Engine = Engine.PHONON
    voice: str = "af_heart"
    phonon_voice: str = "default"
    phonon_language: str = "en"
    phonon_model_dir: str = ""
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

    @property
    def uses_phonon(self) -> bool:
        return self.engine == Engine.PHONON

    def normalized(self) -> "AppSettings":
        self.explain_sentence_count = self.clamped_explain_sentences
        self.speed = min(4.0, max(0.25, float(self.speed)))
        self.phonon_language = (self.phonon_language or "en").strip().lower() or "en"
        self.phonon_voice = (self.phonon_voice or "default").strip() or "default"
        self.phonon_model_dir = (self.phonon_model_dir or "").strip()
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
        data["engine"] = self.engine.value
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
                elif key == "engine":
                    value = Engine(value)
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

# Phonon voices: (name, label, language). "default" is the checkpoint's own voice; the rest come
# from the gradium/phonon-voices catalogue and are downloaded by ptts on first use (250 KB each).
PHONON_LANGUAGES: list[tuple[str, str]] = [
    ("en", "English"),
    ("fr", "French (needs a Phonon 40M model)"),
    ("de", "German (needs a Phonon 40M model)"),
    ("es", "Spanish (needs a Phonon 40M model)"),
    ("pt", "Portuguese (needs a Phonon 40M model)"),
    ("none", "None (read text as written)"),
]
PHONON_VOICES: list[tuple[str, str, str]] = [
    ("default", "Default · built into the model", "any"),
    ("Bryony", "Bryony \u00b7 EN-GB, female", "en"),
    ("Delia", "Delia \u00b7 EN-US, female", "en"),
    ("Elodie-Rose", "Elodie-Rose \u00b7 EN-GB, female", "en"),
    ("Everly", "Everly \u00b7 EN-US, female", "en"),
    ("Freya", "Freya \u00b7 EN-GB, female", "en"),
    ("Hadley", "Hadley \u00b7 EN-US, female", "en"),
    ("Harper", "Harper \u00b7 EN-US, female", "en"),
    ("Imogen", "Imogen \u00b7 EN-GB, female", "en"),
    ("Juniper", "Juniper \u00b7 EN-US, female", "en"),
    ("Lottie", "Lottie \u00b7 EN-GB, female", "en"),
    ("Maeve", "Maeve \u00b7 EN-GB, female", "en"),
    ("Pippa", "Pippa \u00b7 EN-GB, female", "en"),
    ("Zoey", "Zoey \u00b7 EN-US, female", "en"),
    ("Alfie", "Alfie \u00b7 EN-GB, male", "en"),
    ("Easton", "Easton \u00b7 EN-US, male", "en"),
    ("Edwin", "Edwin \u00b7 EN-GB, male", "en"),
    ("Hugh", "Hugh \u00b7 EN-GB, male", "en"),
    ("Micah", "Micah \u00b7 EN-US, male", "en"),
    ("Miles", "Miles \u00b7 EN-US, male", "en"),
    ("Preston", "Preston \u00b7 EN-US, male", "en"),
    ("Russell", "Russell \u00b7 EN-US, male", "en"),
    ("Spencer", "Spencer \u00b7 EN-GB, male", "en"),
    ("Teddy", "Teddy \u00b7 EN-GB, male", "en"),
    ("Toby", "Toby \u00b7 EN-GB, male", "en"),
    ("Trevor", "Trevor \u00b7 EN-US, male", "en"),
    ("Azgorath", "Azgorath \u00b7 CHARACTERS", "en"),
    ("Captain Silas Blackwater", "Captain Silas Blackwater \u00b7 CHARACTERS", "en"),
    ("Fairy Godmother", "Fairy Godmother \u00b7 CHARACTERS", "en"),
    ("Fennick the Young Elf", "Fennick the Young Elf \u00b7 CHARACTERS", "en"),
    ("Grub the Ogre", "Grub the Ogre \u00b7 CHARACTERS", "en"),
    ("Master Aldric", "Master Aldric \u00b7 CHARACTERS", "en"),
    ("Morwenna of the Deep", "Morwenna of the Deep \u00b7 CHARACTERS", "en"),
    ("Old Mortlake", "Old Mortlake \u00b7 CHARACTERS", "en"),
    ("Sir Bramwell", "Sir Bramwell \u00b7 CHARACTERS", "en"),
    ("The Snow Queen", "The Snow Queen \u00b7 CHARACTERS", "en"),
    ("The Thing Under the Stairs", "The Thing Under the Stairs \u00b7 CHARACTERS", "en"),
    ("Vyrmathrax the Ancient", "Vyrmathrax the Ancient \u00b7 CHARACTERS", "en"),
    ("Coralie", "Coralie \u00b7 FR-FR, female", "fr"),
    ("H\u00e9l\u00e8ne", "H\u00e9l\u00e8ne \u00b7 FR-FR, female", "fr"),
    ("Lilou", "Lilou \u00b7 FR-FR, female", "fr"),
    ("Ma\u00eblys", "Ma\u00eblys \u00b7 FR-FR, female", "fr"),
    ("Ninon", "Ninon \u00b7 FR-FR, female", "fr"),
    ("No\u00e9mie", "No\u00e9mie \u00b7 FR-FR, female", "fr"),
    ("Orianne", "Orianne \u00b7 FR-FR, female", "fr"),
    ("Sol\u00e8ne", "Sol\u00e8ne \u00b7 FR-FR, female", "fr"),
    ("Tiphaine", "Tiphaine \u00b7 FR-FR, female", "fr"),
    ("Arnaud", "Arnaud \u00b7 FR-FR, male", "fr"),
    ("Augustin", "Augustin \u00b7 FR-FR, male", "fr"),
    ("Baptiste", "Baptiste \u00b7 FR-FR, male", "fr"),
    ("Jules", "Jules \u00b7 FR-FR, male", "fr"),
    ("Lucien", "Lucien \u00b7 FR-FR, male", "fr"),
    ("Marius", "Marius \u00b7 FR-FR, male", "fr"),
    ("Annika", "Annika \u00b7 DE-DE, female", "de"),
    ("Jette", "Jette \u00b7 DE-DE, female", "de"),
    ("Josefine", "Josefine \u00b7 DE-DE, female", "de"),
    ("Jule", "Jule \u00b7 DE-DE, female", "de"),
    ("Lotta", "Lotta \u00b7 DE-DE, female", "de"),
    ("Nele", "Nele \u00b7 DE-DE, female", "de"),
    ("Svea", "Svea \u00b7 DE-DE, female", "de"),
    ("Anton", "Anton \u00b7 DE-DE, male", "de"),
    ("Henrik", "Henrik \u00b7 DE-DE, male", "de"),
    ("Leon", "Leon \u00b7 DE-DE, male", "de"),
    ("Mats", "Mats \u00b7 DE-DE, male", "de"),
    ("Mika", "Mika \u00b7 DE-DE, male", "de"),
    ("Mirko", "Mirko \u00b7 DE-DE, male", "de"),
    ("Alba", "Alba \u00b7 ES-ES, female", "es"),
    ("Alicia", "Alicia \u00b7 ES-ES, female", "es"),
    ("Azucena", "Azucena \u00b7 ES-MX, female", "es"),
    ("Candela", "Candela \u00b7 ES-ES, female", "es"),
    ("Celia", "Celia \u00b7 ES-ES, female", "es"),
    ("Danae", "Danae \u00b7 ES-MX, female", "es"),
    ("Jimena", "Jimena \u00b7 ES-MX, female", "es"),
    ("Luc\u00eda-Sol", "Luc\u00eda-Sol \u00b7 ES-ES, female", "es"),
    ("Nerea", "Nerea \u00b7 ES-ES, female", "es"),
    ("Noa", "Noa \u00b7 ES-ES, female", "es"),
    ("Rebeca", "Rebeca \u00b7 ES-MX, female", "es"),
    ("Regina", "Regina \u00b7 ES-MX, female", "es"),
    ("Ximena", "Ximena \u00b7 ES-MX, female", "es"),
    ("Aldo", "Aldo \u00b7 ES-MX, male", "es"),
    ("Borja", "Borja \u00b7 ES-ES, male", "es"),
    ("Guillermo", "Guillermo \u00b7 ES-ES, male", "es"),
    ("Iker", "Iker \u00b7 ES-ES, male", "es"),
    ("Izan", "Izan \u00b7 ES-ES, male", "es"),
    ("Lalo", "Lalo \u00b7 ES-MX, male", "es"),
    ("Leonel", "Leonel \u00b7 ES-MX, male", "es"),
    ("Marcos", "Marcos \u00b7 ES-ES, male", "es"),
    ("Nico", "Nico \u00b7 ES-MX, male", "es"),
    ("Nuno", "Nuno \u00b7 ES-ES, male", "es"),
    ("Ulises", "Ulises \u00b7 ES-MX, male", "es"),
    ("Yael", "Yael \u00b7 ES-MX, male", "es"),
    ("Clarice", "Clarice \u00b7 PT-BR, female", "pt"),
    ("D\u00e9bora", "D\u00e9bora \u00b7 PT-BR, female", "pt"),
    ("Fl\u00e1via", "Fl\u00e1via \u00b7 PT-BR, female", "pt"),
    ("Isadora", "Isadora \u00b7 PT-BR, female", "pt"),
    ("Manuela-Lu", "Manuela-Lu \u00b7 PT-BR, female", "pt"),
    ("Rafaela", "Rafaela \u00b7 PT-BR, female", "pt"),
    ("Davi", "Davi \u00b7 PT-BR, male", "pt"),
    ("Igor", "Igor \u00b7 PT-BR, male", "pt"),
    ("Kau\u00e3", "Kau\u00e3 \u00b7 PT-BR, male", "pt"),
    ("Marcelo", "Marcelo \u00b7 PT-BR, male", "pt"),
    ("Ot\u00e1vio", "Ot\u00e1vio \u00b7 PT-BR, male", "pt"),
    ("Vitor", "Vitor \u00b7 PT-BR, male", "pt"),
]


def phonon_voices_for(language: str) -> list[tuple[str, str]]:
    """Voices for the UI: the default, then the language's catalogue voices, then the others."""
    same = [(n, l) for n, l, lang in PHONON_VOICES if lang == "any" or lang == language]
    other = [(n, l) for n, l, lang in PHONON_VOICES if lang != "any" and lang != language]
    return same + other


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
