"""Entry point.

  dynamite-tts                 start the tray app (or raise Settings if it is already running)
  dynamite-tts speak           read the current highlight (what the hotkey runs)
  dynamite-tts toggle-mode     Verbatim -> Summary -> Explain -> Verbatim
  dynamite-tts stop | settings | quit | ping
  dynamite-tts --speak TEXT    speak TEXT (starts the app if needed)
"""
from __future__ import annotations

import sys

from . import VERSION, applog
from .control import COMMANDS, InstanceLock, send_command


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    if args[:1] in (["--version"], ["-v"]):
        print(f"dynamite-tts {VERSION}")
        return 0
    if args[:1] in (["--help"], ["-h"]):
        print(__doc__.strip())
        return 0

    speak_text = None
    if args[:1] == ["--speak"]:
        speak_text = " ".join(args[1:]).strip()
        if not speak_text:
            print("usage: dynamite-tts --speak TEXT", file=sys.stderr)
            return 2
        reply = send_command("speak-text " + speak_text)
        if reply is not None:
            return 0
        args = []

    if args:
        command = args[0]
        if command not in COMMANDS:
            print(f"unknown command {command!r}; one of {', '.join(COMMANDS)}", file=sys.stderr)
            return 2
        reply = send_command(command)
        if reply is None:
            if command in ("speak", "settings"):
                # Hotkey pressed before the app started: start it and act once up.
                return _start_app(initial_command=command)
            print("Dynamite TTS is not running.", file=sys.stderr)
            return 1
        if command == "ping":
            print(reply)
        return 0 if reply.startswith("ok") else 1

    return _start_app(speak_text=speak_text)


def _start_app(speak_text: str | None = None, initial_command: str | None = None) -> int:
    lock = InstanceLock()
    if not lock.claim():
        send_command("settings")
        return 0
    applog.open_log()
    from gi.repository import GLib

    from .app import DynamiteApp

    app = DynamiteApp(speak_text=speak_text)
    if initial_command:
        GLib.idle_add(lambda: app.handle_command(initial_command) and False)
    return app.run()


if __name__ == "__main__":
    sys.exit(main())
