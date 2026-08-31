# NPU TTS Spike

Experiment branch helper for trying **ONNX TTS on the AMD Ryzen AI NPU** (Vitis AI EP).

## Run

```powershell
dotnet run --project experiments/NpuTtsSpike
# or, if models already downloaded:
dotnet run --project experiments/NpuTtsSpike -- --skip-download
```

## What it does

1. Probes for AMD Ryzen AI Software / `RYZEN_AI_INSTALLATION_PATH`
2. Lists ONNX Runtime providers
3. Tries to download `magicunicorn/kokoro-npu-quantized` (currently LFS-pointer-only on HF)
4. Falls back to real **Piper** `en_US-lessac-medium.onnx` for EP tests
5. Attempts sessions: CPU, DirectML GPU, DirectML NPU filter, VitisAI
6. Writes `FINDINGS.md`

## Prerequisite for actual NPU offload

Install [AMD Ryzen AI Software](https://ryzenai.docs.amd.com/), then re-run and watch **Task Manager → NPU**.

## Current machine result (summary)

See `FINDINGS.md` after each run. On this host without Ryzen AI Software:

- Piper ONNX loads on **CPU** and **DirectML GPU**
- **DirectML NPU filter** rejected by stock ORT
- **VitisAI** provider name is recognized but `onnxruntime_providers_vitisai.dll` is missing
