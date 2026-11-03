#!/bin/bash
# Remove the app, its GNOME shortcuts, the autostart entry, and settings. Lemonade and the
# downloaded models stay unless --purge is given.
set -euo pipefail
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts"
EXE="${DATA}/app/bin/dynamite-tts"
if [[ -x "${EXE}" ]]; then
  "${EXE}" quit >/dev/null 2>&1 || true
  "${EXE}" uninstall-shortcuts >/dev/null 2>&1 || true
fi
rm -f "${HOME}/.local/bin/dynamite-tts" \
      "${HOME}/.local/share/applications/dynamite-tts.desktop" \
      "${HOME}/.local/share/icons/hicolor/128x128/apps/dynamite-tts.png"
rm -rf "${DATA}/app" "${XDG_CONFIG_HOME:-$HOME/.config}/dynamite-tts" "${XDG_STATE_HOME:-$HOME/.local/state}/dynamite-tts"
if [[ "${1:-}" == "--purge" ]]; then
  pkill -f "${DATA}/lemonade/lemond" >/dev/null 2>&1 || true
  rm -rf "${DATA}"
  echo "Removed Dynamite TTS, Lemonade and the models."
else
  echo "Removed Dynamite TTS. Lemonade and the models remain in ${DATA}; use --purge to remove them."
fi
