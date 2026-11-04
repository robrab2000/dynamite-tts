#!/bin/bash
# Install Dynamite TTS on an Apple Silicon Mac from a release zip or a local build.
# Install-Dynamite-TTS.command is the double-clickable front for this script.
#
#   ./install.sh [DynamiteTTS.app]    copy the app to ~/Applications and open it
#   ./install.sh --lemonade ...       also install AMD's Lemonade Server and its chat model.
#                                     Optional: Summary and Explain use a built-in model unless
#                                     you choose Lemonade in Settings → Modes.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
LEMONADE=0
SOURCE=""
for arg in "$@"; do
  case "$arg" in
    --lemonade) LEMONADE=1 ;;
    --no-lemonade) LEMONADE=0 ;; # now the default; still accepted
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) SOURCE="$arg" ;;
  esac
done
if [[ -z "${SOURCE}" ]]; then
  for candidate in "${HERE}/DynamiteTTS.app" "${HERE}/../../target/macos/DynamiteTTS.app"; do
    [[ -d "${candidate}" ]] && SOURCE="${candidate}" && break
  done
fi
if [[ -z "${SOURCE}" || ! -d "${SOURCE}" ]]; then
  echo "No DynamiteTTS.app found. Build one with packaging/macos/package-app.sh or pass its path." >&2
  exit 1
fi
if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Dynamite TTS for macOS needs Apple Silicon." >&2
  exit 1
fi

DEST="${HOME}/Applications/DynamiteTTS.app"
echo "== App =="
"${DEST}/Contents/MacOS/DynamiteTTS" quit >/dev/null 2>&1 || true
mkdir -p "${HOME}/Applications"
rm -rf "${DEST}"
cp -R "${SOURCE}" "${DEST}"
# A downloaded app is quarantined; this copy was made on purpose, so clear it.
xattr -dr com.apple.quarantine "${DEST}" 2>/dev/null || true
echo "Installed ${DEST}"

if [[ "${LEMONADE}" == "1" ]]; then
  echo
  echo "== Lemonade (optional; choose it in Settings → Modes) =="
  "${HERE}/install-lemonade.sh"
  LEMOND="${HOME}/Library/Application Support/DynamiteTTS/lemonade/lemond"
  CLI="${HOME}/Library/Application Support/DynamiteTTS/lemonade/lemonade"
  if ! curl -sf --max-time 2 http://127.0.0.1:13305/api/v1/health >/dev/null; then
    nohup "${LEMOND}" --host 127.0.0.1 --port 13305 --no-broadcast >/dev/null 2>&1 &
    for _ in $(seq 1 60); do
      curl -sf --max-time 2 http://127.0.0.1:13305/api/v1/health >/dev/null && break
      sleep 1
    done
  fi
  if [[ -x "${CLI}" ]]; then
    "${CLI}" backends install llamacpp:metal || true
    "${CLI}" pull Llama-3.2-1B-Instruct-GGUF || true
  fi
fi

echo
echo "== Launch =="
open "${DEST}"
echo "Dynamite TTS is in the menu bar. Allow it under System Settings → Privacy & Security → Accessibility"
echo "when asked, then highlight text and press Ctrl+Shift+S. Ctrl+Shift+M cycles Verbatim, Summary, Explain."
