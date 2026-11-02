#!/bin/bash
# Remove the app, shortcuts, autostart entry, and settings. Lemonade, Phonon and their models stay unless --purge is given.
set -euo pipefail
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts"
"${HOME}/.local/bin/dynamite-tts" quit >/dev/null 2>&1 || true
PYTHONPATH="${DATA}/app" python3 - <<'PY' || true
from dynamite_tts.hotkeys import HotkeyBinder
from dynamite_tts import autostart
HotkeyBinder().unbind()
autostart.apply(False)
PY
rm -f "${HOME}/.local/bin/dynamite-tts" \
      "${HOME}/.local/share/applications/dynamite-tts.desktop" \
      "${HOME}/.local/share/icons/hicolor/128x128/apps/dynamite-tts.png"
rm -rf "${DATA}/app" "${XDG_CONFIG_HOME:-$HOME/.config}/dynamite-tts" "${XDG_STATE_HOME:-$HOME/.local/state}/dynamite-tts"
if [[ "${1:-}" == "--purge" ]]; then
  pkill -f "${DATA}/lemonade/lemond" >/dev/null 2>&1 || true
  rm -rf "${DATA}"
  echo "Removed Dynamite TTS, Lemonade and Phonon."
else
  echo "Removed Dynamite TTS. Lemonade (${DATA}/lemonade) and Phonon (${DATA}/phonon-venv) remain; use --purge to remove them."
fi
