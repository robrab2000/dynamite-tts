#!/bin/bash
# Build DynamiteTTS.app (Apple Silicon) from the Rust crate, bundle the Phonon model, and sign it.
#
#   packaging/macos/package-app.sh            -> target/macos/DynamiteTTS.app
#   packaging/macos/package-app.sh --zip      -> also target/macos/DynamiteTTS-macos-arm64-vX.Y.Z.zip
#                                                (the app with the installer and its scripts) and the
#                                                target/macos/Install-Dynamite-TTS-macOS-vX.Y.Z.zip (the
#                                                installer on its own with a README)
#
# CODESIGN_IDENTITY picks the signing identity (default: the first "Apple Development" one, else
# ad hoc). DYNAMITE_BUNDLE_PHONON_MODEL=0 leaves the model out; the app then downloads it once.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${ROOT}/target/macos"
APP="${OUT}/DynamiteTTS.app"
VERSION="$(sed -n 's/^version = "\(.*\)"/\1/p' "${ROOT}/Cargo.toml" | head -n 1)"
export PATH="${PATH}:${HOME}/.cargo/bin:/opt/homebrew/bin"

choose_identity() {
  if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
    printf '%s\n' "$CODESIGN_IDENTITY"
    return
  fi
  local found
  found="$(security find-identity -p codesigning -v 2>/dev/null | sed -n 's/.*"\(Apple Development:.*\)"/\1/p' | head -n 1)"
  printf '%s\n' "${found:--}"
}

if ! command -v cargo >/dev/null; then
  echo "Rust is not installed. Install it from https://rustup.rs (no sudo needed), then run this again." >&2
  exit 1
fi
echo "Building Dynamite TTS ${VERSION}…"
cargo build --release --locked --manifest-path "${ROOT}/Cargo.toml"

rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp "${ROOT}/target/release/dynamite-tts" "${APP}/Contents/MacOS/DynamiteTTS"
sed "s/__VERSION__/${VERSION}/g" "${ROOT}/packaging/macos/Info.plist" > "${APP}/Contents/Info.plist"
cp "${ROOT}/assets/icons/AppIcon.icns" "${APP}/Contents/Resources/AppIcon.icns"

if [[ "${DYNAMITE_BUNDLE_PHONON_MODEL:-1}" != "0" ]]; then
  CACHE="${HOME}/Library/Application Support/DynamiteTTS/models/phonon-31m-en"
  if "${ROOT}/packaging/fetch-phonon-model.sh" "${CACHE}"; then
    mkdir -p "${APP}/Contents/Resources/models/phonon-31m-en"
    cp "${CACHE}/"{config.json,tokenizer.json,model.q8.gguf,default-voice.safetensors} \
      "${APP}/Contents/Resources/models/phonon-31m-en/"
  else
    echo "Could not fetch the Phonon model; the app will download it on first use." >&2
  fi
fi

IDENTITY="$(choose_identity)"
codesign --force --sign "${IDENTITY}" --identifier "com.robrab2000.dynamitett" "${APP}"
codesign --verify --verbose=2 "${APP}"
echo "Built ${APP} (signed with ${IDENTITY})"

if [[ "${1:-}" == "--zip" ]]; then
  NAME="DynamiteTTS-macos-arm64-v${VERSION}"
  STAGE="${OUT}/${NAME}"
  ZIP="${OUT}/${NAME}.zip"
  rm -rf "${STAGE}" "${ZIP}"
  mkdir -p "${STAGE}"
  ditto "${APP}" "${STAGE}/DynamiteTTS.app"
  cp "${ROOT}/packaging/macos/"{install.sh,install-lemonade.sh} "${ROOT}/LICENSE" "${STAGE}/"
  sed "s/__VERSION__/${VERSION}/" "${ROOT}/packaging/macos/Install-Dynamite-TTS.command" > "${STAGE}/Install-Dynamite-TTS.command"
  sed "s/__VERSION__/${VERSION}/g" "${ROOT}/packaging/macos/README-macOS.txt" > "${STAGE}/README.txt"
  chmod +x "${STAGE}/Install-Dynamite-TTS.command" "${STAGE}/"*.sh
  ditto -c -k --norsrc --keepParent "${STAGE}" "${ZIP}"
  echo "Wrote ${ZIP}"
  # The installer on its own, with a README: it downloads the zip above from the GitHub release.
  # It is zipped because a bare download loses its execute bit; Archive Utility puts it back.
  KIT="Install-Dynamite-TTS-macOS-v${VERSION}"
  rm -rf "${OUT:?}/${KIT}" "${OUT}/${KIT}.zip"
  mkdir -p "${OUT}/${KIT}"
  cp "${STAGE}/Install-Dynamite-TTS.command" "${OUT}/${KIT}/"
  sed "s/__VERSION__/${VERSION}/g" "${ROOT}/packaging/macos/README-macOS.txt" > "${OUT}/${KIT}/README.txt"
  ditto -c -k --norsrc --keepParent "${OUT}/${KIT}" "${OUT}/${KIT}.zip"
  echo "Wrote ${OUT}/${KIT}.zip"
  # And the bare installer, for people who'd rather run one file: `bash Install-Dynamite-TTS-v….command`.
  cp "${STAGE}/Install-Dynamite-TTS.command" "${OUT}/Install-Dynamite-TTS-v${VERSION}.command"
  echo "Wrote ${OUT}/Install-Dynamite-TTS-v${VERSION}.command"
fi
