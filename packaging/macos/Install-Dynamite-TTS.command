#!/bin/bash
# The Dynamite TTS installer for Apple Silicon Macs. Double-click it, or run it in Terminal:
#   bash Install-Dynamite-TTS.command
# Terminal shows each step and stays open until you press Return. It uses the DynamiteTTS.app
# next to it, or downloads this version's zip from GitHub; copies the app to ~/Applications;
# optionally installs Lemonade with its chat model for Summary, Explain and ELI5; and opens the app.
#
# Downloads are quarantined, so the first double-click may be refused: choose Open Anyway under
# System Settings → Privacy & Security, or run it from Terminal with bash.
set -euo pipefail

VERSION="__VERSION__"
REPO="robrab2000/dynamite-tts"
HERE="$(cd "$(dirname "$0")" && pwd)"
workdir=""

finish() {
  local status=$?
  [[ -n "${workdir}" ]] && rm -rf "${workdir}"
  echo
  if [[ "${status}" -ne 0 ]]; then
    echo "Setup stopped (exit ${status}). The message above is the failure."
  fi
  echo "Press Return to close this window."
  read -r _ || true
  exit "${status}"
}
trap finish EXIT

echo "Dynamite TTS setup"
echo

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Dynamite TTS for macOS needs an Apple Silicon Mac." >&2
  exit 1
fi
major="$(sw_vers -productVersion | cut -d. -f1)"
if [[ "${major}" -lt 14 ]]; then
  echo "macOS 14 or later is required." >&2
  exit 1
fi

# The release zip has the app and install scripts next to this file. On its own, this file
# downloads that zip.
if [[ -d "${HERE}/DynamiteTTS.app" && -f "${HERE}/install.sh" ]]; then
  SRC="${HERE}"
elif [[ "${VERSION}" == "__"* ]]; then
  echo "DynamiteTTS.app is not next to this file. Build it with packaging/macos/package-app.sh --zip first." >&2
  exit 1
else
  NAME="DynamiteTTS-macos-arm64-v${VERSION}"
  URL="https://github.com/${REPO}/releases/download/v${VERSION}/${NAME}.zip"
  workdir="$(mktemp -d)"
  echo "== Download =="
  echo "${URL}"
  curl -fL --retry 3 --progress-bar -o "${workdir}/${NAME}.zip" "${URL}"
  ditto -x -k "${workdir}/${NAME}.zip" "${workdir}"
  SRC="${workdir}/${NAME}"
  if [[ ! -d "${SRC}/DynamiteTTS.app" ]]; then
    echo "The download did not contain DynamiteTTS.app." >&2
    exit 1
  fi
  echo
fi

args=()
echo "Summary, Explain and ELI5 need Lemonade Server and a small chat model (about 1 GB, downloaded now)."
echo "Verbatim reading works without it."
read -r -p "Install Lemonade? [Y/n] " answer || answer=""
case "${answer}" in
  n|N|no|No|NO) args+=(--no-lemonade) ;;
esac
echo

bash "${SRC}/install.sh" "${args[@]+"${args[@]}"}" "${SRC}/DynamiteTTS.app"
