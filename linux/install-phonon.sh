#!/bin/bash
# Install Gradium's Phonon text-to-speech (the `ptts` wheel) into a private venv under
# ~/.local/share/dynamite-tts/phonon-venv and download its default English model, so the
# "Phonon (in-process)" engine in Settings works offline afterwards. No root needed.
#
#   ./linux/install-phonon.sh             install or upgrade
#   PTTS_VERSION=0.6.0 ./linux/install-phonon.sh   pin a version
set -euo pipefail

DATA="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts"
VENV="${DATA}/phonon-venv"
VERSION="${PTTS_VERSION:-}"

if ! python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)'; then
  echo "Phonon needs Python 3.9 or later."; exit 1
fi
if ! python3 -c 'import venv' 2>/dev/null; then
  echo "python3-venv is missing. On Ubuntu: sudo apt-get install python3-venv"; exit 1
fi
if ! grep -q -E 'avx2' /proc/cpuinfo 2>/dev/null && [[ "$(uname -m)" == "x86_64" ]]; then
  echo "Warning: this CPU has no AVX2. The prebuilt Phonon wheel needs x86-64-v3; it will fall back to a source build (needs Rust)."
fi

mkdir -p "${DATA}"
if [[ ! -x "${VENV}/bin/python" ]]; then
  echo "Creating ${VENV}…"
  python3 -m venv "${VENV}"
fi
"${VENV}/bin/python" -m pip install --quiet --upgrade pip
echo "Installing ptts${VERSION:+==$VERSION}…"
"${VENV}/bin/python" -m pip install --quiet --upgrade "ptts${VERSION:+==$VERSION}"

echo "Downloading the Phonon 31M English model (about 65 MB, once)…"
tmp="$(mktemp --suffix=.wav)"
trap 'rm -f "${tmp}"' EXIT
"${VENV}/bin/python" -m ptts --lang en --quant q8 "Phonon is ready." -o "${tmp}" >/dev/null
echo "Phonon installed: $("${VENV}/bin/python" -c 'import ptts; print(ptts.__version__)')"
echo "Choose Engine → Phonon in Dynamite TTS Settings (or set \"engine\": \"phonon\" in ~/.config/dynamite-tts/settings.json)."
