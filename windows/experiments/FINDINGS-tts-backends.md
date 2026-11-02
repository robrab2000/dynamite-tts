# Local TTS backend findings (2026-09-08)

Host: ROB_AERO — AMD Ryzen AI 9 HX 370 (12c/24t, Radeon 890M iGPU, XDNA2 NPU), NVIDIA RTX 5070 Laptop (Blackwell), driver 610.88, 32 GB RAM.
Model: Kokoro-82M v1.0 ONNX (KokoroSharpBinaries v2.0.0 `kokoro.onnx` fp32 / `kokoro-fp16.onnx`).
Harness: `windows/experiments/KokoroBench*` (`probe` mode drives `InferenceSession` directly). Raw logs live next to the projects (`*.log`, gitignored).

## Why the app felt slow

Two defects in the previous local path, independent of hardware:

1. `KokoroWavSynthesizer.Synthesize(text, voice, OnProgress, OnComplete)` **returns immediately** (it enqueues a job on KokoroSharp's worker thread). The app completed its streaming channel right after that call, so every segment was dropped: the hotkey produced silence while the CPU still rendered the whole paragraph in the background, and the next request queued behind that orphaned job.
2. With the saved settings (DirectML adapter 2, fp16) the first inference threw inside DirectML's `ConvTranspose` **on KokoroSharp's thread**, which terminates the process. A DirectML session *creates* fine, so the failure only shows up on first use.

Even when the KokoroSharp engine path worked, its default segmentation renders one 48-token first segment and then **everything else as a single 510-token block** (~8 s of compute before the second piece of audio), so playback stalled after the first sentence.

## Measured backends (steady state, Kokoro fp32 unless noted)

| Backend | 32 tokens (~2.1 s audio) | 96 tokens (~5.2 s) | 256 tokens (~11.6 s) | Notes |
|---|---:|---:|---:|---|
| CPU, 12 intra-op threads (ORT 1.22) | 0.68 s | 1.58 s | 3.53 s | best CPU config, ~3.3x realtime |
| CPU, 8 threads | 0.91 s | 1.74 s | 3.80 s | KokoroSharp default thread count |
| CPU, 24 threads (all logical) | 1.08 s | 3.67 s | 7.70 s | SMT contention halves throughput |
| CPU, fp16 model, 12 threads | 0.82 s | 1.87 s | 3.90 s | no gain over fp32 on this CPU |
| CPU, onnx-community INT8 (`model_quantized.onnx`) | 2.53 s | 5.59 s | 14.2 s | 3-4x slower than fp32 |
| CPU, onnx-community `model_q8f16.onnx` | 2.30 s | 4.90 s | 8.81 s | slower than fp32 |
| DirectML, RTX 5070 or Radeon 890M, fp16/fp32, ORT 1.22 **and** 1.24.4 | fail | fail | fail | `ConvTranspose` → "The parameter is incorrect" at run time |
| CUDA, RTX 5070, ORT 1.22 (CUDA 12.9 wheels), exhaustive cuDNN search | 0.30 s | 1.11 s | 5.9 s | first call per new shape stalls 10-130 s (PTX JIT for Blackwell + autotune) |
| CUDA, RTX 5070, ORT 1.22, heuristic cuDNN search | 0.17 s | 0.69 s | 3.5 s | |
| **CUDA, RTX 5070, ORT 1.29 (CUDA 13 wheels), heuristic** | **0.14 s** | **0.67 s** | 3.1 s | native Blackwell kernels: only a ~3 s one-off warm-up; 510 tokens still 11.4 s |
| CUDA, RTX 5070, ORT 1.29, fp16 model | 0.13 s | 0.59 s | 3.3 s | no gain over fp32 |
| Ryzen AI NPU (Vitis AI EP) | n/a | n/a | n/a | Ryzen AI Software not installed; `onnxruntime_providers_vitisai.dll` missing (see `NpuTtsSpike/FINDINGS.md`) |

Observations:

- Kokoro's cost on GPU grows faster than linearly with segment length (launch-bound alignment/upsampling ops; only trivial shape ops fall back to CPU). GPU pays off for short segments, which is exactly what the streaming engine now feeds it.
- ORT 1.29's CUDA build links against **CUDA 13** (`cublasLt64_13.dll`); ORT 1.22 links against CUDA 12. Neither needs a system-wide CUDA install: NVIDIA's PyPI wheels (`nvidia-cudnn-cu13`, `nvidia-cublas`, `nvidia-cuda-runtime`, `nvidia-cufft`, `nvidia-cuda-nvrtc`) contain the DLLs (~1.4 GB for CUDA 13) and can sit next to the app.
- A resident CUDA context keeps the dGPU awake (battery/thermals on a laptop) and holds ~0.5-1 GB VRAM; an app that lives in the tray should unload the session after idling.

## What changed in the app

- `LocalTtsService` drives the ONNX session directly: sentence-sized segments (first ≤32 tokens, then ≤120 on CPU / ≤96 on GPU), a bounded hand-off to playback, `RunOptions.Terminate` on cancel, warm-up at startup, fp32 graph preferred whenever it is on disk.
- `AudioPlaybackService.BeginStream` provides one gapless mixer input with back-pressure (8 s look-ahead) instead of one mixer input per segment.
- The app moved from `KokoroSharp.DirectML` (ORT 1.22 DirectML build) to `Microsoft.ML.OnnxRuntime.Gpu.Windows` 1.29. Two side effects: DirectML is gone (it never ran the model), and the newer CPU provider alone roughly doubled CPU throughput (first segment 0.68 s → 0.34 s, paragraph 2.5x → 5x realtime).
- **CUDA engine**: `CudaDeviceService` enumerates NVIDIA GPUs through `nvcuda.dll` (driver API, no DXGI) and checks the driver supports CUDA 13; `CudaRuntimeService` downloads the five pinned NVIDIA wheels (1.03 GB, sha256-verified) and extracts the 16 DLLs to `%LocalAppData%\DynamiteTts\cuda13\bin` (55 s on this connection); `LocalTtsService` keeps the CPU session warm, creates the CUDA session on first use (~3.5 s including a two-length warm-up) while the CPU renders the first sentences, switches per segment as soon as it is ready, and disposes it plus resets the device's primary context after `GpuIdleUnloadMinutes` (default 5) so the dGPU can power down. A stale saved adapter index (e.g. DirectML adapter 2) resolves to CUDA device 0; "CPU only" (device id -2) never touches the GPU.

### Measured end to end (harness `LocalTtsStreamingHarnessTests`, 22 s paragraph, am_adam at 1.28x)

| Path | First audio | Whole paragraph | Realtime factor |
|---|---:|---:|---:|
| Old app (KokoroSharp engine, saved DirectML settings) | never (silence / crash) | — | — |
| CPU, ORT 1.22 DirectML build (first rewrite) | 0.66–0.83 s | ~9 s | 2.0–2.6x |
| CPU, ORT 1.29 GPU build (current, GPU disabled or absent) | 0.33 s | 4.4 s | 5.1x |
| Auto, first utterance after GPU unload (CPU bridges, GPU takes over mid-paragraph) | 0.35 s | 4.0 s | 5.6x |
| Auto, GPU session resident (RTX 5070 Laptop, CUDA 13) | 0.11 s | 1.7 s | 13x |

Cancel: Stop during segment 2 returns within that segment; a follow-up request's first audio arrives ≈0.4 s later. Idle unload → reload cycle verified (`Gpu_UnloadsWhenIdle_AndReloadsOnNextUse`): reload costs the same ~3.5 s bridged by the CPU, GPU throughput identical afterwards.

## Phonon (added 2026-11-02)

Gradium's [Phonon](https://github.com/gradium-ai/xn-ptts) (a 31M/40M flow-matching model with the Mimi decoder, Rust runtime `ptts` 0.6.0) was integrated as a second in-process engine through our own `cdylib` shim (`windows/native/phonon`), because the upstream `ptts-ffi` C API has no speaking-rate control; the shim applies `duration_delta` through `Synth::set_conditions` without a reload. No ONNX export is offered upstream, so ONNX Runtime is not involved.

Measured with the shim on the same host (CPU only, `model.q8.gguf`, default voice):

| Threads | Short sentence (3.4 s audio) | Paragraph (17.4 s audio) | First audio |
|---:|---:|---:|---:|
| 24 (xn default, all logical cores) | 0.63 s (5.5x) | — | 90 ms |
| 6 | 0.40 s (8.5x) | 1.86 s (9.3x) | 22–82 ms |
| 4 (app default) | 0.29–0.53 s (6.5–12x) | 1.5–2.7 s (6–11x) | 15–53 ms |
| 3 | 0.31–0.43 s (8–11x) | 1.52 s (11.4x) | 17–69 ms |

Engine load: 0.06 s (65 MB q8 weights), so there is no idle-unload logic. End to end through `SpeechOrchestrator` + WASAPI (`EndToEndPlaybackHarnessTests.PhononEngine_*`, 20 s paragraph, Easton voice at 1.15x): first audible 356 ms on a cold engine (including the one-off voice download), 24 ms on a warm one; synthesis finished at 12 s of the 20 s utterance with zero underruns; Stop returns within one frame.

Speaking rate is the one real limitation: `duration_delta` -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x (speed ≈ exp(-delta/2)); the app clamps to ±0.8 since the model misreads text beyond that, so the 0.25x–4x slider maps to roughly 0.67x–1.5x on Phonon. Kokoro stays the default engine; Phonon is a setting.

### Phonon on the Mac (Apple M1 Max, 8P+2E cores, macOS 27.0.1)

The same shim builds for arm64 (`libdynamite_phonon.dylib`, 5.5 MB) and the menu-bar app loads it with `dlopen`. On Apple Silicon the crate turns on xn's `accelerate` feature, which hands the f32 matrix products to Accelerate (AMX). With 4 threads and the plain NEON kernels it runs at 25x realtime; Accelerate raises that to 29–33x and splits the output into blocks about half the size. `-C target-cpu=apple-m1` measured within noise of the default arm64 baseline, so there is no Mac counterpart to the x86-64-v3 flag. `kai` (KleidiAI SME2) is left off because only an M4 or newer has SME2.

Shim alone (`macos/Spike/phonon_bench.py`, Accelerate build, default voice):

| Threads | Short sentence (3.76 s audio) | Paragraph (21.9 s audio) | First audio |
|---:|---:|---:|---:|
| 2 (NEON only) | 0.22 s (17x) | 1.25 s (17.5x) | 11 ms |
| 4 | 0.12–0.13 s (29–32x) | 0.67 s (32.8x) | 9–11 ms |
| 6 (app default: performance cores, at most six) | 0.11 s (33.5–34x) | 0.62–0.67 s (32.6–35x) | 9–11 ms |
| 10 (all cores, NEON only) | — | — | about 14x overall; the efficiency cores slow it down |

Through the app's Swift engine (`swift test` in `macos/DynamiteTTS`, `PhononEngineTests`, no audio device): load 85 ms including a one-word priming utterance, first audio 10–24 ms, short sentence 113–128 ms (29–33x), paragraph 676 ms (32.4x). The Easton catalogue voice took 23 ms to first audio and ran at 30x. Stopping right after the first block returned in 8 ms with 0.08 s of audio, and the engine spoke normally again afterwards. For comparison, Lemonade's Metal Kokoro on the same Mac takes 450–890 ms to first PCM at 4–7.5x (`macos/Spike/RESULTS.md`).

Phonon against the previous Mac engine (`macos/Spike/engine_bench.py`, 2026-11-02, 64 GB M1 Max). Kokoro numbers cover `lemond` plus its `koko` backend, with Lemonade's chat model excluded. Phonon numbers cover a process that holds only the shim, minus its 15 MB Python baseline. Memory is the physical footprint, which is what Activity Monitor shows. CPU is CPU-seconds divided by wall time, so "4 cores" means four cores fully busy for that moment.

| | Kokoro (Lemonade "Metal") | Phonon (in-process, 6 threads) |
|---|---:|---:|
| Memory, loaded and idle | 2.39 GB (`koko` 2.3 GB, `lemond` 13 MB) | 0.22 GB |
| Memory, peak while speaking the paragraph | 3.1 GB | 0.33 GB |
| Memory, after 10 utterances | 2.52 GB | 0.33 GB |
| CPU while idle and loaded | 0.04% of one core | 0.1% of one core |
| CPU, short sentence | 3.4 CPU-s (3.6 cores for 0.94 s) | 0.55 CPU-s (4.0 cores for 0.14 s) |
| CPU, paragraph | 22.3 CPU-s (6.6 cores for 3.4 s) | 3.1 CPU-s (4.2 cores for 0.73 s) |
| GPU | 0%; despite the backend's name it runs on the CPU (`"device": "cpu"` in `/api/v1/health`) | 0% |
| Time to first audio, cold (model not loaded) | 2.42 s | 78 ms, including dlopen and the 52 ms load |
| Time to first audio, warm, short sentence | 938 ms (932–963) | 11 ms (10–31) |
| Time to first audio, warm, paragraph | 1.38 s | 11 ms |
| Speed, paragraph | 7.8x realtime | 30x realtime |

Kokoro returns its first audio only after it has synthesized the whole first sentence, so its first-audio time grows with sentence length. Phonon hands over each block of audio (about 0.3–0.4 s) as soon as it is decoded. Per second of speech, Phonon used about 0.14 CPU-s against Kokoro's 0.84.

Upstream also offers a Core ML route (`PhononCore.xcframework` + `phonon-31m-en-coreml.zip`). Its exporter sums the checkpoint's conditions into one fixed vector (`flow_lm.conditions` in `host.safetensors`), so speaking rate can't change at runtime, and its Mimi decoder runs on the CPU anyway. The CPU shim is already about 30x realtime, so the Mac app uses it and keeps the speed slider.

### Phonon against Lemonade CPU Kokoro on Linux (i7-8750H, 6 cores/12 threads, 30 GB, Ubuntu 26.04, 2026-11-02)

This used a harness that calls the app's `PhononStream`, `LemonadeClient` and `PCMStream` the way `speech.py` does, without GTK or playback. Each engine was run twice, in both orders, and the two passes agreed within about 10%. Memory is PSS for the whole process tree (Kokoro is `lemond` plus `koko` plus the client). Phonon used 3 threads. Kokoro used `af_heart`; no chat model was loaded.

| | Kokoro (Lemonade CPU) | Phonon (`ptts` wheel, in-process) |
|---|---:|---:|
| Memory, after load | 1.89 GB | 0.14 GB |
| Memory, peak while speaking | 2.7–3.2 GB | 0.35–0.37 GB |
| Memory, idle after the runs | 2.4–2.9 GB (not returned) | 0.29–0.30 GB |
| CPU while idle and loaded | 0.1–0.3% of one core | 0% |
| CPU, short sentence | 5.8–6.3 CPU-s (about 4 cores) | 1.3 CPU-s (3.3 cores) |
| CPU, paragraph | 48–49 CPU-s (6.6 cores, peaks of about 10) | 7.4 CPU-s (3.3 cores) |
| CPU-s per second of speech | 1.3–1.85 | 0.34 |
| Time to first audio, cold (engine not loaded) | 4.9 s (5.4 s with Lemonade not running) | 0.50 s |
| Time to first audio, warm, short / paragraph | 1.4–1.6 s / 3.1 s | 26 ms / 29 ms |
| Speed, paragraph | 3.6x realtime | 10x realtime |

Phonon's first utterance after loading took 0.22 s to first audio, and the cold figure includes it. The short sentence briefly peaked at about 7.6 cores despite the 3-thread setting.

### Phonon against in-process Kokoro on Windows (rob_aero, 24 logical CPUs with an NVIDIA GPU, 2026-11-02)

This used a harness over the app's `LocalTtsService` and `PhononTtsService`, run 3 times with each engine in a fresh process. "Kokoro auto" is the app default: it starts on the CPU, loads CUDA in the background after the first utterance, and then runs on the GPU. Memory is private bytes.

| | Kokoro, CPU | Kokoro, auto (CUDA) | Phonon |
|---|---:|---:|---:|
| Memory, after load | 642 MB | 655 MB | 142 MB |
| Memory, loaded and idle after the runs | 985 MB | 4.47 GB committed, including 2.0 GB of GPU memory | 237 MB |
| Memory, peak while speaking | 986 MB | 4.47 GB | 346 MB |
| CPU while idle | ~0% | ~0% | ~0% |
| CPU, short sentence | 12.2 CPU-s (12 cores, 50% of the machine) | 0.45 CPU-s (1 core) | 1.6 CPU-s (3.4 cores, 14%) |
| CPU, paragraph | 66 CPU-s (12 cores) | 2.5 CPU-s (1 core), GPU up to 68% | 9.0 CPU-s (3.4 cores) |
| Load | 2.45 s, 7.1 CPU-s | 2.5 s, then 3.2 s of background CUDA load | 0.21 s, 0.64 CPU-s |
| Time to first audio, cold | 2.97 s | 2.99 s | 0.27 s |
| Time to first audio, warm, short / paragraph | 444 / 507 ms | 195 / 199 ms | 26 / 29 ms |
| Speed, paragraph | 4.2x realtime | 9.4x realtime | 8.3x realtime |

## Remaining options

1. **NPU**: not viable for Kokoro today (no Vitis AI EP without AMD Ryzen AI Software, and no evidence its op coverage includes Kokoro's graph).
2. **Different model**: Piper (`windows/experiments/models/en_US-lessac-medium.onnx`, ~60 MB) is far cheaper on CPU but noticeably lower voice quality; not benchmarked for latency here.
3. **Ship the CUDA provider DLL on demand too**: `onnxruntime_providers_cuda.dll` (167 MB) currently ships inside the single-file exe; it could join the runtime download to shrink the executable.
