"""Read the highlighted text.

On Linux a highlight is the PRIMARY selection, so no synthetic Ctrl+C is needed and the
clipboard is never touched. wl-paste handles Wayland (GNOME 47+, wlroots, KDE); xclip or
xsel handle X11; the GTK clipboard is the last resort.
"""
from __future__ import annotations

import os
import shutil
import subprocess


def _run(command: list[str]) -> str | None:
    try:
        result = subprocess.run(command, capture_output=True, timeout=3, check=False)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0:
        return None
    return result.stdout.decode("utf-8", "replace")


def _read_selection(primary: bool) -> str | None:
    wayland = bool(os.environ.get("WAYLAND_DISPLAY"))
    if wayland and shutil.which("wl-paste"):
        command = ["wl-paste", "--no-newline", "--type", "text/plain;charset=utf-8"]
        if primary:
            command.insert(1, "--primary")
        text = _run(command)
        if text is None:
            command = ["wl-paste", "--no-newline"] + (["--primary"] if primary else [])
            text = _run(command)
        if text is not None:
            return text
    if shutil.which("xclip"):
        text = _run(["xclip", "-o", "-selection", "primary" if primary else "clipboard"])
        if text is not None:
            return text
    if shutil.which("xsel"):
        text = _run(["xsel", "-o", "--primary" if primary else "--clipboard"])
        if text is not None:
            return text
    return _gtk_selection(primary)


def _gtk_selection(primary: bool) -> str | None:
    try:
        import gi

        gi.require_version("Gtk", "3.0")
        from gi.repository import Gdk, Gtk
    except (ImportError, ValueError):
        return None
    atom = Gdk.SELECTION_PRIMARY if primary else Gdk.SELECTION_CLIPBOARD
    clipboard = Gtk.Clipboard.get(atom)
    return clipboard.wait_for_text()


def capture() -> str | None:
    """Return the highlighted text, falling back to the clipboard when nothing is highlighted."""
    for primary in (True, False):
        text = _read_selection(primary)
        if text and text.strip():
            return text.strip()
    return None


def missing_tools() -> list[str]:
    wayland = bool(os.environ.get("WAYLAND_DISPLAY"))
    if wayland and not shutil.which("wl-paste"):
        return ["wl-clipboard"]
    if not wayland and not (shutil.which("xclip") or shutil.which("xsel")):
        return ["xclip"]
    return []
