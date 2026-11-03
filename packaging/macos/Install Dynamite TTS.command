#!/bin/bash
# Double-click this file (or run `bash "Install Dynamite TTS.command"` in Terminal) to install the
# DynamiteTTS.app next to it. Terminal opens, prints each stage, and stays open until you press
# Return. It copies the app to ~/Applications, optionally installs Lemonade with its chat model
# for Summary and Explain, and opens the app.
#
# Downloads are quarantined, so the first double-click may be refused: choose Open Anyway under
# System Settings → Privacy & Security, or run it from Terminal with bash.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

finish() {
  local status=$?
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
if [[ ! -d "${HERE}/DynamiteTTS.app" ]]; then
  echo "DynamiteTTS.app is not next to this file. Unzip the whole release and keep them together." >&2
  exit 1
fi

args=()
echo "Summary and Explain need Lemonade Server and a small chat model (about 1 GB, downloaded now)."
echo "Verbatim reading works without it."
read -r -p "Install Lemonade? [Y/n] " answer || answer=""
case "${answer}" in
  n|N|no|No|NO) args+=(--no-lemonade) ;;
esac
echo

# The release zip and a source checkout both have install.sh next to this file.
bash "${HERE}/install.sh" "${args[@]+"${args[@]}"}" "${HERE}/DynamiteTTS.app"
