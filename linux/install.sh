#!/bin/bash
# Install Dynamite TTS for the current user on Linux (GNOME on Wayland or X11; other desktops with manual shortcuts).
#
#   ./linux/install.sh            install system packages (sudo), Lemonade, models, and the app
#   ./linux/install.sh --no-apt   skip the apt step (you installed the packages yourself)
#
# Everything lands under ~/.local, so no root is needed beyond the apt packages.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="${ROOT}/linux"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts"
APP="${DATA}/app"
BIN="${HOME}/.local/bin"
LEMONADE_DIR="${DATA}/lemonade"
SKIP_APT=0
for arg in "$@"; do
  case "$arg" in
    --no-apt) SKIP_APT=1 ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg"; exit 2 ;;
  esac
done

echo "Dynamite TTS setup (Linux)"
echo

echo "== System packages =="
APT_PACKAGES=(python3 python3-gi gir1.2-gtk-3.0 gir1.2-notify-0.7 gir1.2-ayatanaappindicator3-0.1 wl-clipboard pipewire-bin curl)
if [[ -n "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
  APT_PACKAGES+=(xclip)
fi
if [[ "$SKIP_APT" -eq 1 ]]; then
  echo "Skipping apt (--no-apt)."
elif command -v apt-get >/dev/null; then
  echo "Installing: ${APT_PACKAGES[*]}"
  if [[ "$(id -u)" -eq 0 ]]; then
    apt-get install -y "${APT_PACKAGES[@]}"
  else
    sudo apt-get install -y "${APT_PACKAGES[@]}"
  fi
else
  echo "This is not an apt-based system. Install the equivalents of:"
  echo "  ${APT_PACKAGES[*]}"
  echo "then rerun with --no-apt."
  exit 1
fi

echo
echo "== Checking Python bindings =="
python3 - <<'PY'
import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Notify", "0.7")
from gi.repository import Gtk, Notify  # noqa: F401
try:
    gi.require_version("AyatanaAppIndicator3", "0.1")
    from gi.repository import AyatanaAppIndicator3  # noqa: F401
    print("GTK, libnotify, and AppIndicator bindings are available.")
except (ImportError, ValueError):
    print("GTK and libnotify are available. AppIndicator is missing, so there will be no tray icon.")
PY

echo
echo "== App files =="
mkdir -p "${APP}" "${BIN}" "${HOME}/.local/share/applications" "${HOME}/.local/share/icons/hicolor/128x128/apps"
rm -rf "${APP}/dynamite_tts" "${APP}/icons"
cp -R "${SRC}/dynamite_tts" "${SRC}/icons" "${APP}/"
find "${APP}" -name '__pycache__' -type d -prune -exec rm -rf {} +
cp "${SRC}/dynamite-tts" "${APP}/dynamite-tts"
cp "${SRC}/install-phonon.sh" "${APP}/install-phonon.sh"
chmod +x "${APP}/dynamite-tts"
ln -sf "${APP}/dynamite-tts" "${BIN}/dynamite-tts"
cp "${SRC}/icons/dynamite-tts-idle.png" "${HOME}/.local/share/icons/hicolor/128x128/apps/dynamite-tts.png"
sed "s|^Exec=.*|Exec=${BIN}/dynamite-tts|" "${SRC}/dynamite-tts.desktop" > "${HOME}/.local/share/applications/dynamite-tts.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "${HOME}/.local/share/applications" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "${HOME}/.local/share/icons/hicolor" 2>/dev/null || true
echo "Installed to ${APP}; launcher at ${BIN}/dynamite-tts"
case ":${PATH}:" in
  *":${BIN}:"*) ;;
  *) echo "Note: ${BIN} is not on your PATH yet. Log out and in again, or add it to your shell profile." ;;
esac

echo
echo "== Lemonade =="
"${SRC}/install-lemonade.sh"
start_lemonade() {
  if curl -sf --max-time 2 "http://127.0.0.1:13305/api/v1/health" >/dev/null; then
    echo "Lemonade is already running."
    return
  fi
  echo "Starting Lemonade…"
  nohup "${LEMONADE_DIR}/lemond" --host 127.0.0.1 --port 13305 --no-broadcast >>"${DATA}/lemond.log" 2>&1 &
  local i
  for i in $(seq 1 60); do
    if curl -sf --max-time 2 "http://127.0.0.1:13305/api/v1/health" >/dev/null; then
      return
    fi
    sleep 1
  done
  echo "Lemonade did not start. The log is ${DATA}/lemond.log"
  exit 1
}
start_lemonade
cli="${LEMONADE_DIR}/lemonade"

echo
echo "== Backends =="
"${cli}" backends install kokoro:cpu
"${cli}" backends install llamacpp:cpu

echo
echo "== Models =="
echo "Downloading the Kokoro voice and the chat model. This can take a few minutes the first time."
"${cli}" pull kokoro-v1
"${cli}" pull Llama-3.2-1B-Instruct-GGUF

echo
echo "== Phonon (optional in-process engine) =="
if "${SRC}/install-phonon.sh"; then
  echo "Phonon is available under Settings → Engine."
else
  echo "Phonon setup failed; the app still works with Kokoro through Lemonade. Rerun ${SRC}/install-phonon.sh later."
fi

echo
echo "== Launch =="
"${BIN}/dynamite-tts" quit >/dev/null 2>&1 || true
sleep 0.5
nohup "${BIN}/dynamite-tts" >/dev/null 2>&1 &
sleep 2
if "${BIN}/dynamite-tts" ping >/dev/null 2>&1; then
  echo "Dynamite TTS is running in the tray."
else
  echo "Dynamite TTS did not answer; start it from the app grid or run: dynamite-tts"
fi
echo
echo "Highlight some text and press Ctrl+Shift+S. Ctrl+Shift+M cycles Verbatim, Summary, Explain."
echo "On GNOME the shortcuts were added under Settings > Keyboard > Custom Shortcuts."
echo "On other desktops bind these two commands yourself:"
echo "  ${BIN}/dynamite-tts speak"
echo "  ${BIN}/dynamite-tts toggle-mode"
echo "The log is ${XDG_STATE_HOME:-$HOME/.local/state}/dynamite-tts/dynamite.log"
