#!/bin/bash
# Download the Phonon 31M English model (Apache 2.0, 65 MB) into DEST, pinned to the commit the
# ptts 0.6.0 runtime pins and verified by SHA-256. The app downloads the same files on first use
# when no copy ships with it; the packaging scripts run this to bundle the model.
#
#   packaging/fetch-phonon-model.sh DEST
set -euo pipefail

DEST="${1:?usage: fetch-phonon-model.sh DEST}"
REVISION="8c64b55ec301728e2d1695bfea87a08a267d0c2d"
BASE="https://huggingface.co/gradium/phonon-31m-en/resolve/${REVISION}"

# name size sha256 — the same table as MODEL_FILES in src/phonon.rs.
FILES=(
  "config.json 2851 7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06"
  "tokenizer.json 277967 691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936"
  "model.q8.gguf 64512352 a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae"
  "default-voice.safetensors 256136 f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18"
)

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

mkdir -p "${DEST}"
for entry in "${FILES[@]}"; do
  read -r name size sha <<<"${entry}"
  target="${DEST}/${name}"
  if [[ -f "${target}" ]] && [[ "$(wc -c <"${target}" | tr -d ' ')" == "${size}" ]] && [[ "$(sha256 "${target}")" == "${sha}" ]]; then
    echo "${name} is already there."
    continue
  fi
  echo "Downloading ${name} ($((size / 1000000)) MB)…"
  curl -fL --retry 3 -o "${target}.tmp" "${BASE}/${name}"
  actual="$(sha256 "${target}.tmp")"
  if [[ "${actual}" != "${sha}" ]]; then
    rm -f "${target}.tmp"
    echo "${name} failed its checksum (${actual})." >&2
    exit 1
  fi
  mv -f "${target}.tmp" "${target}"
done
echo "Phonon model is in ${DEST}"
