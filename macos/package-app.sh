#!/bin/bash
# Build an arm64 DynamiteTTS.app and sign it with the local Apple Development identity.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
IDENTITY="${CODESIGN_IDENTITY:-Apple Development: rob.homewood@googlemail.com (TN7GW6535H)}"
APP="${ROOT}/macos/DynamiteTTS/DynamiteTTS.app"

swift build -c release --package-path "${ROOT}/macos/DynamiteTTS"
BIN="${ROOT}/macos/DynamiteTTS/.build/release/DynamiteTTS"

rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${BIN}" "${APP}/Contents/MacOS/DynamiteTTS"
cp "${ROOT}/macos/DynamiteTTS/Info.plist" "${APP}/Contents/Info.plist"
codesign --force --sign "${IDENTITY}" --identifier "com.robrab2000.dynamitett" "${APP}"
codesign --verify --verbose=2 "${APP}"
echo "Signed ${APP}"
