"""HTTP client for a local Lemonade server: health, model load/pull, chat, and streaming PCM speech."""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

from . import applog
from .settings import DATA_DIR

KOKORO_MODEL = "kokoro-v1"
NON_CHAT_MARKERS = ("kokoro", "tts", "whisper", "speech", "audio", "moss", "stt", "asr")


class LemonadeError(Exception):
    pass


def discover_lemond() -> Path | None:
    candidates = [
        DATA_DIR / "lemonade" / "lemond",
        Path("/usr/bin/lemond"),
        Path("/usr/local/bin/lemond"),
        Path("/opt/lemonade/lemond"),
    ]
    for candidate in candidates:
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return candidate
    found = shutil.which("lemond")
    return Path(found) if found else None


def discover_cli() -> Path | None:
    candidate = DATA_DIR / "lemonade" / "lemonade"
    if candidate.is_file() and os.access(candidate, os.X_OK):
        return candidate
    found = shutil.which("lemonade")
    return Path(found) if found else None


def _endpoint(base_url: str, path: str) -> str:
    return base_url.rstrip("/") + path


def _shorten(text: str) -> str:
    text = text.strip()
    if not text:
        return "Lemonade request failed."
    return text if len(text) <= 240 else text[:240] + "…"


def _post_json(url: str, payload: dict, timeout: float) -> tuple[int, bytes]:
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode("utf-8"), headers={"Content-Type": "application/json"}, method="POST"
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    except (urllib.error.URLError, OSError, TimeoutError) as error:
        raise LemonadeError(f"Could not reach Lemonade: {error}") from error


class LemonadeClient:
    def __init__(self):
        self._server: subprocess.Popen | None = None

    # -- server ---------------------------------------------------------------
    def is_healthy(self, base_url: str, timeout: float = 2.0) -> bool:
        try:
            with urllib.request.urlopen(_endpoint(base_url, "/api/v1/health"), timeout=timeout) as response:
                return 200 <= response.status < 300
        except (urllib.error.URLError, OSError, ValueError, TimeoutError):
            return False

    def ensure_server(self, base_url: str, cancelled=lambda: False) -> None:
        if self.is_healthy(base_url):
            return
        lemond = discover_lemond()
        if lemond is None:
            raise LemonadeError("Lemonade is not installed. Run linux/install-lemonade.sh, then try again.")
        host, port = _host_port(base_url)
        applog.write(f"starting lemond {lemond} on {host}:{port}")
        log_path = DATA_DIR / "lemond.log"
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        with open(log_path, "ab") as log:
            self._server = subprocess.Popen(
                [str(lemond), "--host", host, "--port", str(port), "--no-broadcast"],
                stdin=subprocess.DEVNULL,
                stdout=log,
                stderr=log,
                start_new_session=True,
            )
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            if cancelled():
                return
            if self.is_healthy(base_url):
                return
            time.sleep(0.25)
        raise LemonadeError(f"Lemonade did not start on {host}:{port}. See {log_path}.")

    # -- models ---------------------------------------------------------------
    def install_backend(self, base_url: str, recipe: str, backend: str) -> None:
        """Installs a Lemonade backend (e.g. kokoro:cpu); returns at once when it is already installed."""
        status, body = _post_json(_endpoint(base_url, "/api/v1/install"), {"recipe": recipe, "backend": backend}, timeout=1800)
        if not 200 <= status < 300:
            raise LemonadeError(_shorten(body.decode("utf-8", "replace") or f"Could not install {recipe}:{backend}."))

    def ensure_model(self, base_url: str, model: str, on_download=None) -> None:
        status, body = _post_json(_endpoint(base_url, "/api/v1/load"), {"model_name": model}, timeout=600)
        if 200 <= status < 300:
            return
        text = body.decode("utf-8", "replace")
        if any(marker in text for marker in ("not found", "unknown_model", "not downloaded")):
            if on_download is not None:
                on_download()
            self.pull(base_url, model)
            status, body = _post_json(_endpoint(base_url, "/api/v1/load"), {"model_name": model}, timeout=600)
            if 200 <= status < 300:
                return
            raise LemonadeError(_shorten(body.decode("utf-8", "replace")))
        raise LemonadeError(_shorten(text))

    def pull(self, base_url: str, model: str) -> None:
        status, body = _post_json(_endpoint(base_url, "/api/v1/pull"), {"model_name": model}, timeout=1800)
        if not 200 <= status < 300:
            raise LemonadeError(_shorten(body.decode("utf-8", "replace") or f"Could not download {model}."))

    def list_models(self, base_url: str) -> list[str]:
        try:
            with urllib.request.urlopen(_endpoint(base_url, "/api/v1/models"), timeout=10) as response:
                root = json.loads(response.read())
        except (urllib.error.URLError, OSError, ValueError, TimeoutError):
            return []
        return [row.get("id") for row in root.get("data", []) if isinstance(row.get("id"), str)]

    def pick_chat_model(self, base_url: str, preferred: str) -> str:
        ids = self.list_models(base_url)
        if not ids:
            return preferred
        for model_id in ids:
            if model_id.lower() == preferred.lower():
                return preferred
        for model_id in ids:
            lower = model_id.lower()
            if not any(marker in lower for marker in NON_CHAT_MARKERS):
                return model_id
        return preferred

    # -- chat -----------------------------------------------------------------
    def chat(self, base_url: str, model: str, system: str, user: str, max_tokens: int | None) -> str:
        payload: dict = {
            "model": model,
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "stream": False,
        }
        if max_tokens and max_tokens > 0:
            payload["max_tokens"] = max_tokens
        status, body = _post_json(_endpoint(base_url, "/api/v1/chat/completions"), payload, timeout=180)
        if not 200 <= status < 300:
            raise LemonadeError(_shorten(body.decode("utf-8", "replace") or "Chat request failed."))
        try:
            root = json.loads(body)
            content = root["choices"][0]["message"]["content"]
        except (ValueError, KeyError, IndexError, TypeError) as error:
            raise LemonadeError("Lemonade returned a chat response without text.") from error
        if not isinstance(content, str):
            raise LemonadeError("The chat model returned a reply Dynamite could not read.")
        content = content.strip()
        if not content:
            raise LemonadeError("The chat model returned an empty reply.")
        return content


def _host_port(base_url: str) -> tuple[str, int]:
    from urllib.parse import urlparse

    parsed = urlparse(base_url)
    return parsed.hostname or "127.0.0.1", parsed.port or 13305


class PCMStream:
    """Streams 16-bit little-endian 24 kHz mono PCM from /api/v1/audio/speech on a worker thread."""

    def __init__(self, on_pcm, on_complete):
        self._on_pcm = on_pcm
        self._on_complete = on_complete
        self._cancelled = threading.Event()
        self._response = None
        self._thread: threading.Thread | None = None

    def start(self, base_url: str, text: str, voice: str, speed: float) -> None:
        payload = {
            "model": KOKORO_MODEL,
            "input": text,
            "voice": voice,
            "speed": speed,
            "stream_format": "audio",
            "response_format": "pcm",
        }
        request = urllib.request.Request(
            _endpoint(base_url, "/api/v1/audio/speech"),
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        self._thread = threading.Thread(target=self._run, args=(request,), name="pcm-stream", daemon=True)
        self._thread.start()

    def cancel(self) -> None:
        self._cancelled.set()
        response = self._response
        if response is not None:
            try:
                response.close()
            except OSError:
                pass

    def _run(self, request) -> None:
        leftover = b""
        try:
            try:
                self._response = urllib.request.urlopen(request, timeout=180)
            except urllib.error.HTTPError as error:
                detail = error.read().decode("utf-8", "replace")
                raise LemonadeError(f"Speech request failed ({error.code}). {_shorten(detail)}") from error
            while not self._cancelled.is_set():
                chunk = self._response.read(8192)
                if not chunk:
                    break
                data = leftover + chunk
                even = len(data) - (len(data) % 2)
                leftover = data[even:]
                if even:
                    self._on_pcm(data[:even])
        except (urllib.error.URLError, OSError, TimeoutError, ValueError, AttributeError) as error:
            # AttributeError: http.client raises it when cancel() closes the response mid-read.
            if not self._cancelled.is_set():
                self._on_complete(LemonadeError(f"Speech stream failed: {error}"))
                return
        except LemonadeError as error:
            if not self._cancelled.is_set():
                self._on_complete(error)
                return
        finally:
            response = self._response
            if response is not None:
                try:
                    response.close()
                except OSError:
                    pass
        self._on_complete(None)
