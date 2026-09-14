# Local TTS backend findings (2026-09-08)

Host: ROB_AERO — AMD Ryzen AI 9 HX 370 (12c/24t, Radeon 890M iGPU, XDNA2 NPU), NVIDIA RTX 5070 Laptop (Blackwell), driver 610.88, 32 GB RAM.
Model: Kokoro-82M v1.0 ONNX (KokoroSharpBinaries v2.0.0 `kokoro.onnx` fp32 / `kokoro-fp16.onnx`).
Harness: `experiments/KokoroBench*` (`probe` mode drives `InferenceSession` directly). Raw logs live next to the projects (`*.log`, gitignored).

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

## Remaining options

1. **NPU**: not viable for Kokoro today (no Vitis AI EP without AMD Ryzen AI Software, and no evidence its op coverage includes Kokoro's graph).
2. **Different model**: Piper (`experiments/models/en_US-lessac-medium.onnx`, ~60 MB) is far cheaper on CPU but noticeably lower voice quality; not benchmarked for latency here.
3. **Ship the CUDA provider DLL on demand too**: `onnxruntime_providers_cuda.dll` (167 MB) currently ships inside the single-file exe; it could join the runtime download to shrink the executable.
