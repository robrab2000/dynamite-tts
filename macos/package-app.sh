#!/bin/bash
# Build an arm64 DynamiteTTS.app and sign it with the local Apple Development identity.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="${ROOT}/macos/DynamiteTTS/DynamiteTTS.app"

choose_identity() {
  if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    printf '%s\n' "$CODESIGN_IDENTITY"
    return
  fi
  local preferred="Apple Development: rob.homewood@googlemail.com (TN7GW6535H)"
  if security find-identity -p codesigning -v 2>/dev/null | grep -F "$preferred" >/dev/null; then
    printf '%s\n' "$preferred"
    return
  fi
  local found
  found="$(security find-identity -p codesigning -v 2>/dev/null | sed -n 's/.*"\(Apple Development:.*\)"/\1/p' | head -n 1)"
  if [[ -n "$found" ]]; then
    printf '%s\n' "$found"
    return
  fi
  printf '%s\n' "-"
}

IDENTITY="$(choose_identity)"

swift build -c release --package-path "${ROOT}/macos/DynamiteTTS"
BIN="${ROOT}/macos/DynamiteTTS/.build/release/DynamiteTTS"

rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${BIN}" "${APP}/Contents/MacOS/DynamiteTTS"
cp "${ROOT}/macos/DynamiteTTS/Info.plist" "${APP}/Contents/Info.plist"
cp "${ROOT}/macos/DynamiteTTS/AppIcon.icns" "${APP}/Contents/Resources/AppIcon.icns"
cp "${ROOT}/macos/DynamiteTTS/Resources/"*.png "${APP}/Contents/Resources/"
codesign --force --sign "${IDENTITY}" --identifier "com.robrab2000.dynamitett" "${APP}"
codesign --verify --verbose=2 "${APP}"
echo "Signed ${APP}"
