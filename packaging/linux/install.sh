#!/bin/bash
# Install Dynamite TTS for the current user on Linux (GNOME on Wayland or X11; other desktops
# with shortcuts bound by hand).
#
#   ./install.sh               install system packages (sudo), the app, Lemonade and its chat model
#   ./install.sh --no-apt      skip apt (you installed the packages yourself)
#   ./install.sh --no-lemonade skip Lemonade (Verbatim mode with Phonon needs none)
#
# Run it from an unpacked release tarball (prebuilt binary) or from a checkout
# (packaging/linux/install.sh builds the binary with cargo). Everything lands under ~/.local.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts"
APP="${DATA}/app"
BIN="${HOME}/.local/bin"
SKIP_APT=0
LEMONADE=1
for arg in "$@"; do
  case "$arg" in
    --no-apt) SKIP_APT=1 ;;
    --no-lemonade) LEMONADE=0 ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg"; exit 2 ;;
  esac
done

# A release tarball has bin/dynamite-tts next to this script; a checkout has Cargo.toml two up.
if [[ -x "${HERE}/bin/dynamite-tts" ]]; then
  PREBUILT="${HERE}/bin/dynamite-tts"
  ROOT="${HERE}"
else
  PREBUILT=""
  ROOT="$(cd "${HERE}/../.." && pwd)"
fi

echo "Dynamite TTS setup (Linux)"
echo
if [[ "$(uname -m)" == "x86_64" ]] && ! grep -qw avx2 /proc/cpuinfo 2>/dev/null; then
  echo "This CPU has no AVX2. The release build needs an x86-64-v3 CPU (2013 or newer)."
  echo "Build from a checkout with RUSTFLAGS=\"-C target-cpu=x86-64\" to run on this machine."
  [[ -n "${PREBUILT}" ]] && exit 1
fi

echo "== System packages =="
RUNTIME=(libgtk-3-0 libayatana-appindicator3-1 libasound2 wl-clipboard xclip curl)
BUILD=(build-essential pkg-config libgtk-3-dev libasound2-dev)
PACKAGES=("${RUNTIME[@]}")
[[ -z "${PREBUILT}" ]] && PACKAGES+=("${BUILD[@]}")
if [[ "$SKIP_APT" -eq 1 ]]; then
  echo "Skipping apt (--no-apt)."
elif command -v apt-get >/dev/null; then
  echo "Installing: ${PACKAGES[*]}"
  if [[ "$(id -u)" -eq 0 ]]; then apt-get install -y "${PACKAGES[@]}"; else sudo apt-get install -y "${PACKAGES[@]}"; fi
else
  echo "This is not an apt-based system. Install the equivalents of:"
  echo "  ${PACKAGES[*]}"
  echo "then rerun with --no-apt."
  exit 1
fi

if [[ -z "${PREBUILT}" ]]; then
  echo
  echo "== Build =="
  export PATH="${PATH}:${HOME}/.cargo/bin"
  if ! command -v cargo >/dev/null; then
    echo "Rust is not installed. Install it from https://rustup.rs (no sudo needed), then run this again."
    exit 1
  fi
  cargo build --release --locked --manifest-path "${ROOT}/Cargo.toml"
  PREBUILT="${ROOT}/target/release/dynamite-tts"
fi

echo
echo "== App files =="
"${BIN}/dynamite-tts" quit >/dev/null 2>&1 || true
mkdir -p "${APP}/bin" "${BIN}" "${HOME}/.local/share/applications" "${HOME}/.local/share/icons/hicolor/128x128/apps"
install -m 0755 "${PREBUILT}" "${APP}/bin/dynamite-tts"
ln -sf "${APP}/bin/dynamite-tts" "${BIN}/dynamite-tts"
ICON="${ROOT}/assets/icons/idle.png"
[[ -f "${HERE}/dynamite-tts.png" ]] && ICON="${HERE}/dynamite-tts.png"
cp "${ICON}" "${HOME}/.local/share/icons/hicolor/128x128/apps/dynamite-tts.png"
sed "s|^Exec=.*|Exec=${APP}/bin/dynamite-tts|" "${HERE}/dynamite-tts.desktop" > "${HOME}/.local/share/applications/dynamite-tts.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "${HOME}/.local/share/applications" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "${HOME}/.local/share/icons/hicolor" 2>/dev/null || true
echo "Installed to ${APP}; launcher at ${BIN}/dynamite-tts"
case ":${PATH}:" in
  *":${BIN}:"*) ;;
  *) echo "Note: ${BIN} is not on your PATH yet. Log out and in again, or add it to your shell profile." ;;
esac

echo
echo "== Phonon model (default speech engine) =="
MODEL="${APP}/models/phonon-31m-en"
if [[ -f "${HERE}/models/phonon-31m-en/model.q8.gguf" ]]; then
  mkdir -p "${MODEL}"
  cp "${HERE}/models/phonon-31m-en/"* "${MODEL}/"
  echo "Copied the bundled model to ${MODEL}"
elif [[ -x "${HERE}/fetch-phonon-model.sh" || -x "${ROOT}/packaging/fetch-phonon-model.sh" ]]; then
  FETCH="${HERE}/fetch-phonon-model.sh"
  [[ -x "${FETCH}" ]] || FETCH="${ROOT}/packaging/fetch-phonon-model.sh"
  "${FETCH}" "${MODEL}" || echo "The model download failed; the app downloads it the first time it speaks."
fi

if [[ "${LEMONADE}" == "1" ]]; then
  echo
  echo "== Lemonade (Summary and Explain) =="
  "${HERE}/install-lemonade.sh"
  LEMONADE_DIR="${DATA}/lemonade"
  if ! curl -sf --max-time 2 "http://127.0.0.1:13305/api/v1/health" >/dev/null; then
    echo "Starting Lemonade…"
    nohup "${LEMONADE_DIR}/lemond" --host 127.0.0.1 --port 13305 --no-broadcast >>"${DATA}/lemond.log" 2>&1 &
    for _ in $(seq 1 60); do
      curl -sf --max-time 2 "http://127.0.0.1:13305/api/v1/health" >/dev/null && break
      sleep 1
    done
  fi
  if [[ -x "${LEMONADE_DIR}/lemonade" ]]; then
    "${LEMONADE_DIR}/lemonade" backends install llamacpp:cpu || true
    echo "Downloading the chat model. This can take a few minutes the first time."
    "${LEMONADE_DIR}/lemonade" pull Llama-3.2-1B-Instruct-GGUF || true
  fi
fi

echo
echo "== Launch =="
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
