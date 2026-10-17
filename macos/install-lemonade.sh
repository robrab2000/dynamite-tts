#!/bin/bash
# Install the arm64 Lemonade embeddable into Application Support.
# The Homebrew cask needs an administrator password; this archive does not.
set -euo pipefail

VERSION="${LEMONADE_VERSION:-2026.42.1}"
DEST="${HOME}/Library/Application Support/DynamiteTTS/lemonade"
URL="https://github.com/lemonade-sdk/lemonade/releases/download/v${VERSION}/lemonade-embeddable-${VERSION}-macos-arm64.tar.gz"

if [[ -x "${DEST}/lemond" ]]; then
  echo "Lemonade is already at ${DEST}"
  exit 0
fi

mkdir -p "${DEST}"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT
curl -fL --retry 3 -o "${tmp}/lemonade.tar.gz" "${URL}"
tar -xzf "${tmp}/lemonade.tar.gz" -C "${DEST}" --strip-components=1
echo "Installed ${DEST}/lemond"
echo "Start it with: \"${DEST}/lemond\" --host 127.0.0.1 --port 13305 --no-broadcast"
