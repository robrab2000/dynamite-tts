"""Single-instance lock and the Unix control socket that hotkey commands talk to."""
from __future__ import annotations

import fcntl
import os
import socket
import threading
from pathlib import Path

from gi.repository import GLib

from . import applog
from .settings import DATA_DIR, runtime_dir

COMMANDS = ("speak", "toggle-mode", "stop", "settings", "quit", "ping")


def socket_path() -> Path:
    return runtime_dir() / "dynamite-tts.sock"


class InstanceLock:
    def __init__(self):
        self._handle = None

    def claim(self) -> bool:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        handle = open(DATA_DIR / "instance.lock", "a+")
        try:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            handle.close()
            return False
        self._handle = handle
        return True


def send_command(command: str, timeout: float = 3.0) -> str | None:
    """Send one command to the running app. Returns its reply, or None when nothing is listening."""
    path = socket_path()
    if not path.exists():
        return None
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(timeout)
            client.connect(str(path))
            client.sendall(command.encode("utf-8") + b"\n")
            reply = client.recv(4096).decode("utf-8", "replace").strip()
            return reply or "ok"
    except OSError:
        return None


class ControlServer:
    def __init__(self, handler):
        self._handler = handler  # callable(command: str) -> str, runs on the GTK main thread
        self._socket: socket.socket | None = None

    def start(self) -> None:
        path = socket_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        try:
            path.unlink()
        except FileNotFoundError:
            pass
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        server.bind(str(path))
        os.chmod(path, 0o600)
        server.listen(8)
        self._socket = server
        threading.Thread(target=self._accept_loop, name="control-socket", daemon=True).start()
        applog.write(f"control socket {path}")

    def stop(self) -> None:
        if self._socket is not None:
            try:
                self._socket.close()
            except OSError:
                pass
            self._socket = None
        try:
            socket_path().unlink()
        except OSError:
            pass

    def _accept_loop(self) -> None:
        server = self._socket
        while server is not None:
            try:
                connection, _ = server.accept()
            except OSError:
                return
            threading.Thread(target=self._serve, args=(connection,), daemon=True).start()

    def _serve(self, connection: socket.socket) -> None:
        with connection:
            try:
                connection.settimeout(2)
                command = connection.recv(256).decode("utf-8", "replace").strip()
            except OSError:
                return
            done = threading.Event()
            reply: list[str] = []

            def dispatch() -> bool:
                try:
                    reply.append(self._handler(command) or "ok")
                except Exception as error:  # noqa: BLE001
                    reply.append(f"error: {error}")
                done.set()
                return False

            GLib.idle_add(dispatch)
            done.wait(timeout=10)
            try:
                connection.sendall((reply[0] if reply else "timeout").encode("utf-8") + b"\n")
            except OSError:
                pass
