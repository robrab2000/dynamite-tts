#!/bin/bash
# Install the Lemonade embeddable build (no root needed) into ~/.local/share/dynamite-tts/lemonade.
set -euo pipefail

VERSION="${LEMONADE_VERSION:-2026.44.1}"
DEST="${XDG_DATA_HOME:-$HOME/.local/share}/dynamite-tts/lemonade"

case "$(uname -m)" in
  x86_64) ARCH="x64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *) echo "Unsupported CPU architecture: $(uname -m)"; exit 1 ;;
esac
URL="https://github.com/lemonade-sdk/lemonade/releases/download/v${VERSION}/lemonade-embeddable-${VERSION}-ubuntu-${ARCH}.tar.gz"

if [[ -x "${DEST}/lemond" ]]; then
  echo "Lemonade is already at ${DEST}"
  exit 0
fi

mkdir -p "${DEST}"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT
echo "Downloading Lemonade ${VERSION} (${ARCH})…"
curl -fL --retry 3 -o "${tmp}/lemonade.tar.gz" "${URL}"
tar -xzf "${tmp}/lemonade.tar.gz" -C "${DEST}" --strip-components=1
echo "Installed ${DEST}/lemond"
echo "Start it with: \"${DEST}/lemond\" --host 127.0.0.1 --port 13305 --no-broadcast"
