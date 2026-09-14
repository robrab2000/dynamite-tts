# KokoroBench

Console harness for measuring Kokoro synthesis latency and throughput on this machine.
Run it from its output folder so the bundled `voices` directory is found; hard-link or copy
`kokoro.onnx` / `kokoro-fp16.onnx` from the repo root next to the executable so nothing is re-downloaded.

```powershell
dotnet build experiments/KokoroBench -c Release
cd experiments/KokoroBench/bin/Release/net9.0-windows
.\KokoroBench.exe all                       # KokoroSharp engine: CPU + every DXGI adapter via DirectML
.\KokoroBench.exe cpu                       # CPU only
.\KokoroBench.exe dml "RTX 5070"            # substring filter on the config name
.\KokoroBench.exe probe kokoro.onnx cpu 12 1 32,96,256          # raw ORT session: model, backend, intra, inter, token lengths
.\KokoroBench.exe probe kokoro.onnx dml:0 8 1 32                # DirectML adapter 0 (fails on our thread instead of crashing)
.\KokoroBench.exe api                       # dump KokoroSharp's public API
```

`probe` bypasses KokoroSharp's job queue and drives `InferenceSession` directly, so it can
measure per-segment-length cost, thread counts, alternative ONNX files (e.g. the onnx-community
quantized graphs, whose token input is named `input_ids`), and catch execution-provider failures.

## Results on ROB_AERO (Ryzen AI 9 HX 370, RTX 5070 Laptop, 2026-09-08)

| Backend | Result |
|---|---|
| DirectML, any adapter, fp16 or fp32, ORT 1.22 and 1.24.4 | Fails at runtime in `ConvTranspose` ("The parameter is incorrect"). Session creation succeeds, so the failure only appears on first inference. |
| CPU fp32, 12 intra-op threads | ~3.3x realtime; 32-token segment ≈ 0.7 s, 96 tokens ≈ 1.6 s, 256 tokens ≈ 3.5 s |
| CPU fp32, 8 threads | ~3.0x realtime |
| CPU fp32, 24 threads (all logical) | ~1.5x realtime (SMT contention) |
| CPU fp16, 12 threads | ~3.0x realtime (no gain over fp32 on this CPU) |
| onnx-community `model_quantized.onnx` (INT8), 8 threads | ~0.8x realtime (3-4x slower than fp32) |

| CUDA (`../KokoroBenchCuda`, ORT 1.22 + CUDA 12 wheels), heuristic cuDNN search | 32 tokens ≈ 0.17 s, 96 ≈ 0.69 s, 256 ≈ 3.5 s; 10-130 s JIT stall per new shape on first use |
| CUDA (`../KokoroBenchCuda129`, ORT 1.29 + CUDA 13 wheels), heuristic | 32 tokens ≈ 0.14 s, 96 ≈ 0.67 s, 256 ≈ 3.1 s, 510 ≈ 11.4 s; one ~3 s warm-up |

The CUDA variants take a `cuda:<device>[:exhaustive|heuristic|default][:verbose]` backend and need the
runtime DLLs from NVIDIA's PyPI wheels on `PATH` (`../cuda-runtime/bin` for CUDA 12, `../cuda-runtime/bin13`
for CUDA 13; both gitignored). See `../FINDINGS-tts-backends.md` for the full comparison.

Detailed logs: `bench-all.log`, `bench-dml.log`, `../probe.log`, `../probe-cuda*.log`, `harness-before.log`, `harness-after.log`.
