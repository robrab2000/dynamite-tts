"""PCM playback through PipeWire (pw-play), PulseAudio (paplay), or ALSA (aplay).

Audio is written to the player's stdin as it arrives, so speech starts on the first
chunk and stopping kills the process, which drops anything still buffered.
"""
from __future__ import annotations

import json
import math
import queue
import shutil
import struct
import subprocess
import threading
import time
from dataclasses import dataclass

SAMPLE_RATE = 24_000
BYTES_PER_SAMPLE = 2
_FRAME_SECONDS = 0.02
_FRAME_BYTES = int(SAMPLE_RATE * _FRAME_SECONDS) * BYTES_PER_SAMPLE


@dataclass
class AudioOutputDevice:
    uid: str
    name: str


def list_output_devices() -> list[AudioOutputDevice]:
    devices: list[AudioOutputDevice] = []
    if shutil.which("pw-dump"):
        try:
            raw = subprocess.run(["pw-dump"], capture_output=True, text=True, timeout=5, check=False).stdout
            for node in json.loads(raw or "[]"):
                props = node.get("info", {}).get("props", {}) if isinstance(node, dict) else {}
                if props.get("media.class") != "Audio/Sink":
                    continue
                name = props.get("node.name")
                if name:
                    devices.append(AudioOutputDevice(name, props.get("node.description") or name))
        except (OSError, ValueError, subprocess.TimeoutExpired):
            pass
    if not devices and shutil.which("pactl"):
        try:
            raw = subprocess.run(["pactl", "list", "short", "sinks"], capture_output=True, text=True, timeout=5, check=False).stdout
            for line in raw.splitlines():
                parts = line.split("\t")
                if len(parts) >= 2:
                    devices.append(AudioOutputDevice(parts[1], parts[1]))
        except (OSError, subprocess.TimeoutExpired):
            pass
    return devices


def _player_command(device: str) -> list[str] | None:
    if shutil.which("pw-play"):
        command = ["pw-play", "--raw", "--rate", str(SAMPLE_RATE), "--channels", "1", "--format", "s16"]
        if device:
            command += ["--target", device]
        return command + ["-"]
    if shutil.which("paplay"):
        command = ["paplay", "--raw", f"--rate={SAMPLE_RATE}", "--channels=1", "--format=s16le"]
        if device:
            command.append(f"--device={device}")
        return command
    if shutil.which("aplay"):
        command = ["aplay", "-q", "-t", "raw", "-f", "S16_LE", "-r", str(SAMPLE_RATE), "-c", "1"]
        if device:
            command += ["-D", device]
        return command + ["-"]
    return None


class PlaybackError(Exception):
    pass


class PCMPlayer:
    """Feeds PCM to the player process from a writer thread so the GTK main loop never blocks on a full pipe."""

    _END = object()

    def __init__(self):
        self._lock = threading.Lock()
        self._process: subprocess.Popen | None = None
        self._queue: queue.Queue | None = None
        self._device = ""
        self._generation = 0
        self._started_at: float | None = None
        self._levels: list[float] = []
        self._pending = b""
        self._bytes_written = 0
        self.on_drained = None

    def prepare(self, device: str) -> None:
        with self._lock:
            self._stop_locked()
            command = _player_command(device)
            if command is None:
                raise PlaybackError("No audio player found. Install pipewire-bin (pw-play), pulseaudio-utils (paplay), or alsa-utils (aplay).")
            self._device = device
            self._generation += 1
            try:
                process = subprocess.Popen(
                    command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
                )
            except OSError as error:
                raise PlaybackError(f"Could not start {command[0]}: {error}") from error
            self._process = process
            self._queue = queue.Queue()
            self._started_at = None
            self._levels = []
            self._pending = b""
            self._bytes_written = 0
            threading.Thread(
                target=self._writer, args=(process, self._queue, self._generation), name="pcm-writer", daemon=True
            ).start()

    def enqueue(self, data: bytes) -> None:
        """Thread-safe: may be called from the network thread."""
        with self._lock:
            if self._queue is None:
                return
            self._record_levels(data)
            self._queue.put(data)

    def finish(self) -> None:
        """No more audio is coming; on_drained fires once the player has played everything."""
        with self._lock:
            if self._queue is not None:
                self._queue.put(self._END)

    def stop(self, play_chime: bool) -> None:
        with self._lock:
            had_audio = self._bytes_written > 0
            self._stop_locked()
            device = self._device
        if play_chime and had_audio:
            self._play_chime(device)

    @property
    def is_active(self) -> bool:
        with self._lock:
            return self._process is not None

    @property
    def level(self) -> float:
        with self._lock:
            if self._started_at is None or not self._levels:
                return 0.0
            index = int((time.monotonic() - self._started_at) / _FRAME_SECONDS)
            if index >= len(self._levels):
                return 0.0
            return self._levels[index]

    def _writer(self, process: subprocess.Popen, pending: "queue.Queue", generation: int) -> None:
        stdin = process.stdin
        drained = False
        while True:
            item = pending.get()
            if item is self._END:
                try:
                    stdin.close()
                except OSError:
                    pass
                process.wait()
                with self._lock:
                    if self._generation == generation and self._process is process:
                        self._process = None
                        self._queue = None
                        drained = True
                break
            try:
                stdin.write(item)
                stdin.flush()
            except (BrokenPipeError, OSError):
                break
            with self._lock:
                if self._started_at is None:
                    self._started_at = time.monotonic()
                self._bytes_written += len(item)
        if drained and self.on_drained:
            self.on_drained()

    def _record_levels(self, data: bytes) -> None:
        buffer = self._pending + data
        offset = 0
        while offset + _FRAME_BYTES <= len(buffer):
            frame = buffer[offset : offset + _FRAME_BYTES]
            samples = struct.unpack(f"<{len(frame) // 2}h", frame)
            rms = math.sqrt(sum(s * s for s in samples) / len(samples)) / 32768.0
            self._levels.append(min(1.0, rms * 4))
            offset += _FRAME_BYTES
        self._pending = buffer[offset:]

    def _stop_locked(self) -> None:
        process = self._process
        self._process = None
        if self._queue is not None:
            self._queue.put(self._END)  # unblock the writer; it will see the generation changed
        self._queue = None
        self._generation += 1
        if process is None:
            return
        try:
            if process.stdin:
                process.stdin.close()
        except OSError:
            pass
        try:
            process.kill()
        except OSError:
            pass
        threading.Thread(target=process.wait, daemon=True).start()

    def _play_chime(self, device: str) -> None:
        command = _player_command(device)
        if command is None:
            return
        samples = bytearray()
        length = int(SAMPLE_RATE * 0.16)
        for i in range(length):
            t = i / SAMPLE_RATE
            envelope = math.exp(-t * 18) * 0.35
            value = math.sin(2 * math.pi * 880 * t) * envelope
            samples += struct.pack("<h", int(max(-1.0, min(1.0, value)) * 32767))

        def run():
            try:
                subprocess.run(command, input=bytes(samples), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5, check=False)
            except (OSError, subprocess.TimeoutExpired):
                pass

        threading.Thread(target=run, name="chime", daemon=True).start()
