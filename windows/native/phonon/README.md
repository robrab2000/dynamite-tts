# dynamite_phonon — the Phonon engine shim

A small Rust `cdylib` that exposes [Gradium's Phonon](https://github.com/gradium-ai/xn-ptts) text-to-speech runtime (the `ptts` crate) to the Windows app over a C ABI. The app P/Invokes it from `windows/src/DynamiteTts/Native/PhononNative.cs`; the header `include/dynamite_phonon.h` documents every call.

Why not the upstream `ptts-ffi` crate: it fixes the speaking rate at the model's default and selects the voice per handle. Dynamite has a speed slider and switches voices per utterance, so this crate drives `ptts::synth::Synth` directly and adds `dp_set_duration_delta` (Phonon's rate condition, applied without a reload), a per-call voice, a process-wide thread count, and 32-bit-integer booleans so no binding has to guess the width of a C `bool`.

## Build

Needs [Rust](https://rustup.rs) (stable, 1.85 or later for edition 2024). From this folder:

```powershell
cargo build --release
```

That writes `target\release\dynamite_phonon.dll` (about 7.5 MB). The app project picks it up from there when it exists (`DynamiteTts.csproj`), `publish.ps1` builds it when `cargo` is on the PATH, and the release workflow builds it on every tag. Without the DLL the app still builds and runs; the Phonon engine then reports "runtime missing" in Settings and the Kokoro engine remains available.

On an Apple Silicon Mac the same command writes `target/release/libdynamite_phonon.dylib`, which `macos/package-app.sh` builds and copies into `DynamiteTTS.app/Contents/Frameworks`. The macOS app binds it from Swift (`macos/DynamiteTTS/Sources/DynamiteTTS/PhononEngine.swift`) using this header's types through a module map. On Apple arm64, `Cargo.toml` enables ptts's `accelerate` feature, about 30% faster on an M1 Max; measurements are in `windows/experiments/FINDINGS-tts-backends.md`.

`.cargo/config.toml` targets x86-64-v3 (AVX2, FMA, F16C), the same baseline as Gradium's own Windows binaries. The DLL will not run on CPUs older than roughly 2013 (Haswell / Excavator); drop the flag to build for an older machine, at the cost of the q8 kernels' AVX paths.

## Model files

The library never downloads anything. `dp_new` takes a checkpoint folder laid out as the model's Hugging Face repo: `config.json`, `tokenizer.json`, `model.q8.gguf` (or `model.safetensors`), `default-voice.safetensors`, and optional `voices/*.safetensors`. The app bundles `phonon-31m-en` (Apache 2.0) when it sits in the repo root under `models/phonon-31m-en` and otherwise downloads it once into `%LocalAppData%\DynamiteTts\models`, pinned to the commit the `ptts` runtime pins and verified by SHA-256. Catalogue voices come from `gradium/phonon-voices` on first use.

## Measured (Ryzen AI 9 HX 370, phonon-31m-en q8, 2026-11-02)

| Threads | Short sentence (3.4 s audio) | Paragraph (17.4 s audio) | First audio |
|---:|---:|---:|---:|
| 24 (xn default) | 0.63 s | — | 90 ms |
| 6 | 0.40 s | 1.86 s (9.3x) | 22-82 ms |
| 4 | 0.29-0.53 s | 1.5-2.7 s (6-11x) | 15-53 ms |
| 3 | 0.31-0.43 s | 1.52 s (11.4x) | 17-69 ms |

Load: 0.06 s. Speaking rate via `duration_delta`: -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x (speed ≈ exp(-0.5·delta)); the app clamps to ±0.8.
