#!/bin/bash
# Build one installer file. Run it from Terminal with bash; Finder will quarantine a double-click.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-${ROOT}/DynamiteTTS-install.command}"
template="${ROOT}/macos/installer.sh.in"
archive="$(mktemp)"
encoded="$(mktemp)"
trap 'rm -f "$archive" "$encoded"' EXIT

# Build the Phonon shim here so the other Mac needs no Rust; package-app.sh there rebuilds it
# from the same source and Cargo.lock if Rust happens to be installed.
PHONON_LIB="windows/native/phonon/target/release/libdynamite_phonon.dylib"
export PATH="${PATH}:${HOME}/.cargo/bin:/opt/homebrew/bin"
phonon_files=(
  windows/native/phonon/Cargo.toml
  windows/native/phonon/Cargo.lock
  windows/native/phonon/build.rs
  windows/native/phonon/.cargo
  windows/native/phonon/src
  windows/native/phonon/include
)
if command -v cargo >/dev/null 2>&1; then
  cargo build --release --locked --manifest-path "${ROOT}/windows/native/phonon/Cargo.toml"
fi
if [[ -f "${ROOT}/${PHONON_LIB}" ]]; then
  phonon_files+=("${PHONON_LIB}")
else
  echo "No Phonon shim built (Rust is not installed); the installed app will not be able to speak with Phonon." >&2
fi

COPYFILE_DISABLE=1 tar -czf "$archive" \
  --exclude '.build' \
  --exclude '*.app' \
  --exclude '*.wav' \
  -C "$ROOT" \
  macos/package-app.sh \
  macos/install-lemonade.sh \
  macos/install-phonon-model.sh \
  macos/DynamiteTTS/Package.swift \
  macos/DynamiteTTS/Info.plist \
  macos/DynamiteTTS/AppIcon.icns \
  macos/DynamiteTTS/Resources \
  macos/DynamiteTTS/Sources \
  macos/DynamiteTTS/Tests \
  "${phonon_files[@]}"
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
