using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DynamiteTts.NpuTtsSpike;

internal static class Program
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ModelsDir = Path.Combine(RepoRoot, "experiments", "models");

    // Primary target (currently broken LFS on HF — spike detects and falls back)
    private const string KokoroHfRepo = "magicunicorn/kokoro-npu-quantized";
    private const string KokoroInt8 = "kokoro-npu-quantized-int8.onnx";

    // Known-good ONNX TTS for EP probing
    private const string PiperRepo = "rhasspy/piper-voices";
    private const string PiperRelPath = "en/en_US/lessac/medium/en_US-lessac-medium.onnx";
    private const string PiperLocalName = "en_US-lessac-medium.onnx";

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Dynamite TTS — NPU TTS Spike (Vitis AI / Ryzen AI) ===");
        Console.WriteLine($"Machine: {Environment.MachineName}  OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Repo: {RepoRoot}");
        Console.WriteLine();

        Directory.CreateDirectory(ModelsDir);

        PrintEnvironmentProbe();
        PrintOrtProviders();

        if (!args.Contains("--skip-download", StringComparer.OrdinalIgnoreCase))
        {
            await EnsureModelsAsync();
        }

        var modelPath = ResolveModelPath();
        if (modelPath == null)
        {
            Console.WriteLine("ERROR: No usable ONNX model in experiments/models/.");
            return 2;
        }

        Console.WriteLine($"Using model: {modelPath} ({new FileInfo(modelPath).Length / (1024.0 * 1024.0):F1} MB)");
        Console.WriteLine();

        InspectModel(modelPath);

        var results = new List<BackendResult>
        {
            TryBackend("CPU", modelPath, o => o.AppendExecutionProvider_CPU(1)),
            TryBackend("DirectML-GPU-0", modelPath, o =>
            {
                o.AppendExecutionProvider("DML", new Dictionary<string, string>
                {
                    ["device_filter"] = "gpu",
                    ["device_id"] = "0"
                });
            }),
            TryBackend("DirectML-filter-npu", modelPath, o =>
            {
                o.AppendExecutionProvider("DML", new Dictionary<string, string>
                {
                    ["device_filter"] = "npu"
                });
            }),
            TryBackend("VitisAIExecutionProvider", modelPath, o =>
            {
                var ryzenAi = DetectRyzenAiInstallPath();
                var providerOptions = new Dictionary<string, string>
                {
                    ["cache_dir"] = Path.Combine(ModelsDir, "vitisai_cache"),
                    ["cache_key"] = Path.GetFileNameWithoutExtension(modelPath)
                };
                var config = FindVaipConfig(ryzenAi);
                if (config != null) providerOptions["config_file"] = config;
                var xclbin = FindXclbin(ryzenAi);
                if (xclbin != null) providerOptions["xclbin"] = xclbin;

                Console.WriteLine($"  VitisAI options: {JsonSerializer.Serialize(providerOptions)}");
                o.AppendExecutionProvider("VitisAIExecutionProvider", providerOptions);
            })
        };

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        foreach (var r in results)
        {
            Console.WriteLine($"[{(r.Success ? "OK" : "FAIL")}] {r.Name}: {r.Message}");
            if (r.Success)
                Console.WriteLine($"       session={r.SessionCreateMs:F0}ms  dry-run={r.InferenceMs:F0}ms");
        }

        var reportPath = Path.Combine(RepoRoot, "experiments", "NpuTtsSpike", "FINDINGS.md");
        WriteFindings(reportPath, results, modelPath);
        Console.WriteLine();
        Console.WriteLine($"Wrote {reportPath}");

        var anyVitis = results.Any(r => r.Success && r.Name.Contains("Vitis", StringComparison.OrdinalIgnoreCase));
        if (!anyVitis)
        {
            Console.WriteLine();
            Console.WriteLine("NEXT: Install AMD Ryzen AI Software so onnxruntime_providers_vitisai.dll is available,");
            Console.WriteLine("      set RYZEN_AI_INSTALLATION_PATH, then re-run this spike and watch Task Manager → NPU.");
            return 1;
        }

        return 0;
    }

    private static string? ResolveModelPath()
    {
        foreach (var name in new[] { KokoroInt8, PiperLocalName })
        {
            var path = Path.Combine(ModelsDir, name);
            if (IsUsableOnnx(path))
                return path;
        }

        return Directory.EnumerateFiles(ModelsDir, "*.onnx")
            .FirstOrDefault(IsUsableOnnx);
    }

    private static bool IsUsableOnnx(string path)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length < 1024) return false;
        // Reject Git LFS pointer files
        using var fs = File.OpenRead(path);
        var buf = new byte[64];
        var read = fs.Read(buf, 0, buf.Length);
        var head = Encoding.ASCII.GetString(buf, 0, read);
        return !head.StartsWith("version https://git-lfs.github.com", StringComparison.Ordinal);
    }

    private static void PrintEnvironmentProbe()
    {
        Console.WriteLine("--- Environment probe ---");
        var ryzen = DetectRyzenAiInstallPath();
        Console.WriteLine($"RYZEN_AI_INSTALLATION_PATH env: {Environment.GetEnvironmentVariable("RYZEN_AI_INSTALLATION_PATH") ?? "(not set)"}");
        Console.WriteLine($"Detected Ryzen AI path: {ryzen ?? "(not found)"}");
        Console.WriteLine($"XLNX_VART_FIRMWARE: {Environment.GetEnvironmentVariable("XLNX_VART_FIRMWARE") ?? "(not set)"}");

        if (ryzen != null)
        {
            Console.WriteLine($"vaip_config: {FindVaipConfig(ryzen) ?? "(missing)"}");
            Console.WriteLine($"xclbin: {FindXclbin(ryzen) ?? "(missing)"}");
            foreach (var dll in Directory.EnumerateFiles(ryzen, "*vitisai*.dll", SearchOption.AllDirectories).Take(5))
                Console.WriteLine($"  dll: {dll}");
        }

        Console.WriteLine();
    }

    private static void PrintOrtProviders()
    {
        Console.WriteLine("--- ONNX Runtime available providers ---");
        try
        {
            foreach (var p in OrtEnv.Instance().GetAvailableProviders())
                Console.WriteLine($"  - {p}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (failed: {ex.Message})");
        }
        Console.WriteLine();
    }

    private static async Task EnsureModelsAsync()
    {
        Console.WriteLine("--- Downloading models via huggingface_hub ---");

        // Try Kokoro NPU quantized (often only LFS pointers on HF today)
        TryHfDownload(KokoroHfRepo, KokoroInt8, Path.Combine(ModelsDir, KokoroInt8));
        if (!IsUsableOnnx(Path.Combine(ModelsDir, KokoroInt8)))
        {
            Console.WriteLine($"  WARN: {KokoroInt8} is missing or is a Git LFS pointer only.");
            Console.WriteLine("        magicunicorn/kokoro-npu-quantized appears not to host real weights.");
        }

        // Always ensure Piper exists as a real ONNX for EP probing
        if (!IsUsableOnnx(Path.Combine(ModelsDir, PiperLocalName)))
        {
            TryHfDownload(PiperRepo, PiperRelPath, Path.Combine(ModelsDir, PiperLocalName));
        }
        else
        {
            Console.WriteLine($"  cached: {PiperLocalName}");
        }

        await Task.CompletedTask;
        Console.WriteLine();
    }

    private static void TryHfDownload(string repoId, string filename, string destPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                ArgumentList =
                {
                    "-c",
                    "from huggingface_hub import hf_hub_download; import shutil, os; " +
                    $"p=hf_hub_download({JsonSerializer.Serialize(repoId)}, {JsonSerializer.Serialize(filename)}); " +
                    $"print(p); print(os.path.getsize(p)); shutil.copy2(p, {JsonSerializer.Serialize(destPath)})"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(600_000);
            Console.WriteLine($"  hf {repoId}/{filename}");
            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine("    " + string.Join("\n    ", stdout.Trim().Split('\n').TakeLast(3)));
            if (proc.ExitCode != 0)
                Console.WriteLine("    stderr: " + stderr.Trim());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  hf download failed: {ex.Message}");
        }
    }

    private static void InspectModel(string modelPath)
    {
        Console.WriteLine("--- Model I/O ---");
        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_CPU(1);
            using var session = new InferenceSession(modelPath, options);
            foreach (var input in session.InputMetadata)
                Console.WriteLine($"  IN  {input.Key}: {input.Value.ElementType.Name} dims=[{string.Join(",", input.Value.Dimensions)}]");
            foreach (var output in session.OutputMetadata)
                Console.WriteLine($"  OUT {output.Key}: {output.Value.ElementType.Name} dims=[{string.Join(",", output.Value.Dimensions)}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  inspect failed: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static BackendResult TryBackend(string name, string modelPath, Action<SessionOptions> configure)
    {
        Console.WriteLine($"--- Backend: {name} ---");
        var sw = Stopwatch.StartNew();
        try
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };
            configure(options);

            using var session = new InferenceSession(modelPath, options);
            var createMs = sw.Elapsed.TotalMilliseconds;
            var inferMs = TryDryRun(session);

            Console.WriteLine($"  SUCCESS create={createMs:F0}ms dry-run={inferMs:F0}ms");
            return new BackendResult(name, true, "session created", createMs, inferMs);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {msg}");
            return new BackendResult(name, false, msg, sw.Elapsed.TotalMilliseconds, 0);
        }
    }

    private static double TryDryRun(InferenceSession session)
    {
        try
        {
            var inputs = new List<NamedOnnxValue>();
            foreach (var (name, meta) in session.InputMetadata)
            {
                var dims = meta.Dimensions.Select(d => d <= 0 ? 1 : Math.Min(d, 64)).ToArray();

                if (meta.ElementType == typeof(long) || meta.ElementType == typeof(Int64))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(dims)));
                else if (meta.ElementType == typeof(int) || meta.ElementType == typeof(Int32))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(dims)));
                else if (meta.ElementType == typeof(float))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(dims)));
                else
                {
                    Console.WriteLine($"  skip dry-run: unsupported type {meta.ElementType.Name} for {name}");
                    return -1;
                }
            }

            var sw = Stopwatch.StartNew();
            using var results = session.Run(inputs);
            _ = results.ToList();
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  dry-run skipped/failed: {ex.Message}");
            return -1;
        }
    }

    private static string? DetectRyzenAiInstallPath()
    {
        var env = Environment.GetEnvironmentVariable("RYZEN_AI_INSTALLATION_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        string[] candidates =
        [
            @"C:\Program Files\RyzenAI",
            @"C:\Program Files\AMD\RyzenAI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RyzenAI"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RyzenAI")
        ];

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
                return c;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(@"C:\Program Files", "*Ryzen*", SearchOption.TopDirectoryOnly))
            {
                if (Directory.EnumerateFiles(dir, "*vitisai*.dll", SearchOption.AllDirectories).Any())
                    return dir;
            }
        }
        catch { }

        return null;
    }

    private static string? FindVaipConfig(string? ryzenAi) =>
        ryzenAi == null ? null : Directory.EnumerateFiles(ryzenAi, "vaip_config.json", SearchOption.AllDirectories).FirstOrDefault();

    private static string? FindXclbin(string? ryzenAi)
    {
        if (ryzenAi == null) return null;
        var bins = Directory.EnumerateFiles(ryzenAi, "*.xclbin", SearchOption.AllDirectories).ToList();
        return bins.FirstOrDefault(b => b.Contains("stx", StringComparison.OrdinalIgnoreCase)
                                     || b.Contains("strix", StringComparison.OrdinalIgnoreCase)
                                     || b.Contains("krk", StringComparison.OrdinalIgnoreCase))
               ?? bins.FirstOrDefault();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DynamiteTts.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static void WriteFindings(string path, List<BackendResult> results, string modelPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# NPU TTS Spike — Findings");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTimeOffset.Now:u}");
        sb.AppendLine($"Host: `{Environment.MachineName}`");
        sb.AppendLine($"Model used: `{modelPath}` ({new FileInfo(modelPath).Length / (1024.0 * 1024.0):F1} MB)");
        sb.AppendLine($"Ryzen AI path: `{DetectRyzenAiInstallPath() ?? "NOT FOUND"}`");
        sb.AppendLine($"ORT providers: `{string.Join(", ", OrtEnv.Instance().GetAvailableProviders())}`");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine("Determine whether an ONNX TTS model can run on the AMD Ryzen AI NPU (HX 370 / XDNA2) via Vitis AI EP on Windows.");
        sb.AppendLine();
        sb.AppendLine("## Model notes");
        sb.AppendLine("- `magicunicorn/kokoro-npu-quantized` currently publishes **Git LFS pointer files only** (~134 bytes). Real INT8/FP16 weights are not downloadable from that repo.");
        sb.AppendLine("- Spike falls back to **Piper** `en_US-lessac-medium.onnx` (~60 MB) as a real ONNX TTS graph for EP probing.");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine("| Backend | Status | Detail | Session ms | Infer ms |");
        sb.AppendLine("|---|---|---|---:|---:|");
        foreach (var r in results)
        {
            var detail = r.Message.Replace("|", "/", StringComparison.Ordinal);
            if (detail.Length > 140) detail = detail[..137] + "...";
            sb.AppendLine($"| {r.Name} | {(r.Success ? "OK" : "FAIL")} | {detail} | {r.SessionCreateMs:F0} | {r.InferenceMs:F0} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Interpretation");
        var vitisOk = results.Any(r => r.Success && r.Name.Contains("Vitis", StringComparison.OrdinalIgnoreCase));
        var cpuOk = results.Any(r => r.Success && r.Name == "CPU");
        var dmlOk = results.Any(r => r.Success && r.Name.StartsWith("DirectML-GPU", StringComparison.Ordinal));

        if (!vitisOk)
        {
            sb.AppendLine("- **Vitis AI EP DLL is not installed** (`onnxruntime_providers_vitisai.dll` missing). ORT knows the provider name but cannot load it without AMD Ryzen AI Software.");
            sb.AppendLine("- DirectML `device_filter=npu` remains rejected by stock ORT builds.");
        }
        else
        {
            sb.AppendLine("- Vitis AI session creation succeeded. Next: verify Task Manager NPU utilization and operator partitioning (`vitisai_ep_report.json`).");
        }

        if (cpuOk) sb.AppendLine("- CPU EP can load the TTS ONNX (baseline).");
        if (dmlOk) sb.AppendLine("- DirectML GPU EP can load the TTS ONNX (matches Dynamite's current path).");

        sb.AppendLine();
        sb.AppendLine("## Next steps for NPU TTS");
        sb.AppendLine("1. Install [AMD Ryzen AI Software](https://ryzenai.docs.amd.com/) and NPU drivers.");
        sb.AppendLine("2. Re-run `dotnet run --project experiments/NpuTtsSpike`.");
        sb.AppendLine("3. If VitisAI session OK, inspect `vitisai_ep_report.json` for NPU vs CPU node assignment.");
        sb.AppendLine("4. Only if meaningful NPU offload: wire a Piper/Vitis path into Dynamite as an experimental engine.");
        sb.AppendLine();
        sb.AppendLine("## Re-run");
        sb.AppendLine("```powershell");
        sb.AppendLine("dotnet run --project experiments/NpuTtsSpike");
        sb.AppendLine("```");

        File.WriteAllText(path, sb.ToString());
    }

    private sealed record BackendResult(
        string Name,
        bool Success,
        string Message,
        double SessionCreateMs,
        double InferenceMs);
}
