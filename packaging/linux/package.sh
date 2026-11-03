#!/bin/bash
# Build the Linux release tarball: target/linux/DynamiteTts-linux-x64-vX.Y.Z.tar.gz with the
# binary, the Phonon model, and the install scripts.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(sed -n 's/^version = "\(.*\)"/\1/p' "${ROOT}/Cargo.toml" | head -n 1)"
NAME="DynamiteTts-linux-x64-v${VERSION}"
STAGE="${ROOT}/target/linux/${NAME}"

cargo build --release --locked --manifest-path "${ROOT}/Cargo.toml"
rm -rf "${STAGE}"
mkdir -p "${STAGE}/bin"
install -m 0755 "${ROOT}/target/release/dynamite-tts" "${STAGE}/bin/dynamite-tts"
"${ROOT}/packaging/fetch-phonon-model.sh" "${STAGE}/models/phonon-31m-en"
cp "${ROOT}/packaging/linux/"{install.sh,uninstall.sh,install-lemonade.sh,dynamite-tts.desktop} "${STAGE}/"
cp "${ROOT}/packaging/fetch-phonon-model.sh" "${STAGE}/"
cp "${ROOT}/assets/icons/app.png" "${STAGE}/dynamite-tts.png"
cp "${ROOT}/LICENSE" "${ROOT}/README.md" "${STAGE}/"
tar -czf "${ROOT}/target/linux/${NAME}.tar.gz" -C "${ROOT}/target/linux" "${NAME}"
echo "Wrote target/linux/${NAME}.tar.gz"
