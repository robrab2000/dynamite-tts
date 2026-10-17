#!/bin/bash
# Build one double-clickable installer. Terminal opens and runs the setup.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-${ROOT}/DynamiteTTS-install.command}"
template="${ROOT}/macos/installer.sh.in"
archive="$(mktemp)"
encoded="$(mktemp)"
trap 'rm -f "$archive" "$encoded"' EXIT

COPYFILE_DISABLE=1 tar -czf "$archive" \
  --exclude '.build' \
  --exclude '*.app' \
  -C "$ROOT" \
  macos/package-app.sh \
  macos/install-lemonade.sh \
  macos/DynamiteTTS/Package.swift \
  macos/DynamiteTTS/Info.plist \
  macos/DynamiteTTS/AppIcon.icns \
  macos/DynamiteTTS/Resources \
  macos/DynamiteTTS/Sources
base64 < "$archive" > "$encoded"

python3 - "$template" "$encoded" "$OUT" <<'PY'
import pathlib, sys
template, encoded, out = map(pathlib.Path, sys.argv[1:])
text = template.read_text()
marker = "@@PAYLOAD@@"
if marker not in text:
    raise SystemExit(f"{template} is missing {marker}")
payload = encoded.read_text().strip() + "\n"
pathlib.Path(out).write_text(text.replace(marker, payload, 1))
PY

chmod +x "$OUT"
echo "Wrote ${OUT}"
