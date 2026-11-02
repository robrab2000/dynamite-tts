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

# The Phonon engine shim (windows/native/phonon). Built with the pinned Cargo.lock when Rust is
# installed; otherwise a prebuilt copy (make-installer.sh ships one) is used. Without either the
# app builds without Phonon; speaking then reports the shim as missing until this is rerun.
PHONON_CRATE="${ROOT}/windows/native/phonon"
PHONON_LIB="${PHONON_CRATE}/target/release/libdynamite_phonon.dylib"
export PATH="${PATH}:${HOME}/.cargo/bin:/opt/homebrew/bin"
if command -v cargo >/dev/null 2>&1; then
  echo "Building the Phonon shim…"
  cargo build --release --locked --manifest-path "${PHONON_CRATE}/Cargo.toml"
elif [[ -f "${PHONON_LIB}" ]]; then
  echo "Rust is not installed; using the prebuilt Phonon shim."
else
  echo "Rust is not installed and there is no prebuilt Phonon shim; Phonon speech will be unavailable." >&2
  echo "Install Rust (https://rustup.rs, no sudo needed) and run this again to add Phonon." >&2
fi

# The Phonon model (65 MB), downloaded once into Application Support, hash-checked, and copied
# into the bundle so the app is ready offline. DYNAMITE_BUNDLE_PHONON_MODEL=0 skips it; the app
# then downloads the same files on first use.
PHONON_MODEL="${HOME}/Library/Application Support/DynamiteTTS/models/phonon-31m-en"
bundle_model=0
if [[ -f "${PHONON_LIB}" && "${DYNAMITE_BUNDLE_PHONON_MODEL:-1}" != "0" ]]; then
  if "${ROOT}/macos/install-phonon-model.sh" "${PHONON_MODEL}"; then
    bundle_model=1
  else
    echo "Could not fetch the Phonon model; the app will download it on first use." >&2
  fi
fi

swift build -c release --package-path "${ROOT}/macos/DynamiteTTS"
BIN="${ROOT}/macos/DynamiteTTS/.build/release/DynamiteTTS"

rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${BIN}" "${APP}/Contents/MacOS/DynamiteTTS"
cp "${ROOT}/macos/DynamiteTTS/Info.plist" "${APP}/Contents/Info.plist"
cp "${ROOT}/macos/DynamiteTTS/AppIcon.icns" "${APP}/Contents/Resources/AppIcon.icns"
cp "${ROOT}/macos/DynamiteTTS/Resources/"*.png "${APP}/Contents/Resources/"
if [[ -f "${PHONON_LIB}" ]]; then
  mkdir -p "${APP}/Contents/Frameworks"
  cp "${PHONON_LIB}" "${APP}/Contents/Frameworks/"
  codesign --force --sign "${IDENTITY}" "${APP}/Contents/Frameworks/libdynamite_phonon.dylib"
fi
if [[ "${bundle_model}" == "1" ]]; then
  mkdir -p "${APP}/Contents/Resources/models/phonon-31m-en"
  for name in config.json tokenizer.json model.q8.gguf default-voice.safetensors; do
    cp "${PHONON_MODEL}/${name}" "${APP}/Contents/Resources/models/phonon-31m-en/"
  done
fi
codesign --force --sign "${IDENTITY}" --identifier "com.robrab2000.dynamitett" "${APP}"
codesign --verify --verbose=2 "${APP}"
echo "Signed ${APP}"
