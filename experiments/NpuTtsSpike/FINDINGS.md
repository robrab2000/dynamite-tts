# NPU TTS Spike — Findings

Generated: 2026-08-31 23:26:22Z
Host: `ROB_AERO`
Model used: `D:\Projects\dynamite-tts\experiments\models\en_US-lessac-medium.onnx` (60.3 MB)
Ryzen AI path: `NOT FOUND`
ORT providers: `DmlExecutionProvider, CPUExecutionProvider`

## Goal
Determine whether an ONNX TTS model can run on the AMD Ryzen AI NPU (HX 370 / XDNA2) via Vitis AI EP on Windows.

## Model notes
- `magicunicorn/kokoro-npu-quantized` currently publishes **Git LFS pointer files only** (~134 bytes). Real INT8/FP16 weights are not downloadable from that repo.
- Spike falls back to **Piper** `en_US-lessac-medium.onnx` (~60 MB) as a real ONNX TTS graph for EP probing.

## Results
| Backend | Status | Detail | Session ms | Infer ms |
|---|---|---|---:|---:|
| CPU | OK | session created | 1859 | 35 |
| DirectML-GPU-0 | OK | session created | 1989 | 2280 |
| DirectML-filter-npu | FAIL | [ErrorCode:Fail] D:\a\_work\1\s\onnxruntime\core\providers\dml\dml_provider_factory.cc:398 onnxruntime::ParseFilter Invalid Filter provid... | 3 | 0 |
| VitisAIExecutionProvider | FAIL | [ErrorCode:Fail] SessionOptionsAppendExecutionProvider_VitisAI: Failed to load shared library | 35 | 0 |

## Interpretation
- **Vitis AI EP DLL is not installed** (`onnxruntime_providers_vitisai.dll` missing). ORT knows the provider name but cannot load it without AMD Ryzen AI Software.
- DirectML `device_filter=npu` remains rejected by stock ORT builds.
- CPU EP can load the TTS ONNX (baseline).
- DirectML GPU EP can load the TTS ONNX (matches Dynamite's current path).

## Next steps for NPU TTS
1. Install [AMD Ryzen AI Software](https://ryzenai.docs.amd.com/) and NPU drivers.
2. Re-run `dotnet run --project experiments/NpuTtsSpike`.
3. If VitisAI session OK, inspect `vitisai_ep_report.json` for NPU vs CPU node assignment.
4. Only if meaningful NPU offload: wire a Piper/Vitis path into Dynamite as an experimental engine.

## Re-run
```powershell
dotnet run --project experiments/NpuTtsSpike
```
