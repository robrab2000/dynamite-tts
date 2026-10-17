using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Services;
using Xunit;
using Xunit.Abstractions;

namespace DynamiteTts.Tests;

/// <summary>
/// Drives the real LocalTtsService streaming path end to end and reports timings.
/// Slow (loads the Kokoro model; the GPU cases download the CUDA runtime once), so it is tagged
/// Perf; run with --filter Category=Perf.
/// </summary>
[Trait("Category", "Perf")]
public class LocalTtsStreamingHarnessTests
{
    private const string SampleText =
        "The quick brown fox jumps over the lazy dog. This is a benchmark of local text to speech synthesis. " +
        "We want to know how long it takes before the first audio segment is ready, and how long the whole " +
        "paragraph takes to render. A typical highlighted paragraph from a web page is roughly this long, " +
        "with a handful of sentences of varying length, some short, and some that run on for a little while " +
        "before finally coming to an end. That should be enough text to make the numbers meaningful.";

    private readonly ITestOutputHelper _output;

    public LocalTtsStreamingHarnessTests(ITestOutputHelper output)
    {
        _output = output;
        EnsureModelsPresent();
    }

    [Fact]
    public async Task CudaRuntime_InstallsOnDemand()
    {
        var probe = CudaDeviceService.Probe();
        _output.WriteLine($"CUDA probe: driver {probe.DriverCudaVersion}, devices={probe.Devices.Count}, unavailability='{probe.Unavailability}'");
        foreach (var d in probe.Devices)
            _output.WriteLine($"  [{d.Ordinal}] {d.Name} sm_{d.ComputeMajor}{d.ComputeMinor} {d.TotalMemoryBytes / (1024 * 1024)} MB");

        if (!probe.IsUsable)
        {
            _output.WriteLine("No usable NVIDIA GPU; skipping runtime install.");
            return;
        }

        var runtime = new CudaRuntimeService();
        var sw = Stopwatch.StartNew();
        var lastPct = -1;
        runtime.ProgressChanged += p =>
        {
            var pct = (int)(p * 100);
            if (pct / 5 != lastPct / 5) _output.WriteLine($"  {sw.Elapsed.TotalSeconds,6:F0}s  {pct,3}%");
            lastPct = pct;
        };

        var wasInstalled = CudaRuntimeService.IsInstalled();
        await runtime.EnsureInstalledAsync();
        _output.WriteLine($"runtime {(wasInstalled ? "already present" : "installed")} in {sw.Elapsed.TotalSeconds:F0}s at {CudaRuntimeService.BinDirectory}");
        Assert.True(CudaRuntimeService.IsInstalled());
    }

    [Theory]
    [InlineData(true, CudaDeviceService.AutoDeviceId, "float32")]    // Auto: GPU when available, CPU bridges the load
    [InlineData(false, CudaDeviceService.AutoDeviceId, "float32")]   // GPU disabled
    [InlineData(true, CudaDeviceService.CpuOnlyDeviceId, "float16")] // CPU only (fp32 graph still preferred when on disk)
    public async Task Streaming_ReportsTimings(bool useGpu, int deviceId, string precision)
    {
        using var service = new LocalTtsService { AutoDownloadGpuRuntime = false };
        service.StatusChanged += s => _output.WriteLine($"    [status] {service.State}: {s}");

        var sw = Stopwatch.StartNew();
        await service.InitializeAsync(useGpu, deviceId, precision);
        _output.WriteLine($"[{useGpu}/{deviceId}/{precision}] init+warmup {sw.ElapsedMilliseconds} ms -> {service.State}: {service.ActiveDeviceDescription} (cpu threads: {service.CpuThreads})");

        for (int run = 1; run <= 3; run++)
        {
            var (firstMs, totalMs, segments, audio) = await RunOnce(service, deviceId, precision, run);
            Assert.True(segments >= 3, "expected sentence-sized segments");
            Assert.True(audio > 15, "expected the whole paragraph to be rendered");
            Assert.True(firstMs < 2000, $"first audio took {firstMs:F0} ms");

            // Give a pending GPU load the chance to finish before the next run so run 2/3 measure the GPU.
            await service.WaitForGpuLoadAsync();
            _output.WriteLine($"    after run{run}: {service.State} (GPU active: {service.IsGpuActive})");
        }

        var gpuExpected = useGpu && deviceId != CudaDeviceService.CpuOnlyDeviceId &&
                          CudaDeviceService.Probe().IsUsable && CudaRuntimeService.IsInstalled();
        Assert.Equal(gpuExpected, service.IsGpuActive);
    }

    [Fact]
    public async Task Gpu_UnloadsWhenIdle_AndReloadsOnNextUse()
    {
        if (!CudaDeviceService.Probe().IsUsable || !CudaRuntimeService.IsInstalled())
        {
            _output.WriteLine("GPU or CUDA runtime not available; skipping.");
            return;
        }

        using var service = new LocalTtsService { AutoDownloadGpuRuntime = false, GpuIdleTimeout = TimeSpan.Zero };
        service.StatusChanged += s => _output.WriteLine($"    [status] {service.State}: {s}");
        await service.InitializeAsync(true, CudaDeviceService.AutoDeviceId, "float32");

        await RunOnce(service, CudaDeviceService.AutoDeviceId, "float32", 1);
        await service.WaitForGpuLoadAsync();
        Assert.True(service.IsGpuActive, "GPU session should be resident after the first utterance");

        var gpuRun = await RunOnce(service, CudaDeviceService.AutoDeviceId, "float32", 2);

        Assert.True(service.UnloadIdleGpu(), "idle unload should dispose the GPU session");
        Assert.False(service.IsGpuActive);
        Assert.Equal(LocalEngineState.GpuIdle, service.State);

        // Next use: CPU bridges again, GPU reloads (exercises the primary-context reset + re-create path).
        var bridged = await RunOnce(service, CudaDeviceService.AutoDeviceId, "float32", 3);
        await service.WaitForGpuLoadAsync();
        Assert.True(service.IsGpuActive, "GPU session should reload after being unloaded");
        Assert.Equal(LocalEngineState.GpuActive, service.State);

        var gpuRun2 = await RunOnce(service, CudaDeviceService.AutoDeviceId, "float32", 4);
        Assert.True(gpuRun2.totalMs < bridged.totalMs * 1.2, "a fully GPU run should not be slower than the bridged run");
        _output.WriteLine($"GPU run before unload {gpuRun.totalMs:F0} ms, after reload {gpuRun2.totalMs:F0} ms");
    }

    [Fact]
    public async Task Streaming_CancelsPromptly()
    {
        using var service = new LocalTtsService { AutoDownloadGpuRuntime = false };
        await service.InitializeAsync(useGpu: false, deviceId: CudaDeviceService.AutoDeviceId, precision: "float32");

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        int segments = 0;

        var task = service.GenerateSpeechStreamingAsync(
            SampleText, "am_adam", 1.0, CudaDeviceService.AutoDeviceId, "float32",
            onSegmentAsync: _ =>
            {
                if (++segments == 2) cts.Cancel();
                return Task.CompletedTask;
            },
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var afterCancelMs = sw.Elapsed.TotalMilliseconds;
        _output.WriteLine($"cancelled after {segments} segments at {afterCancelMs:F0} ms");

        // A follow-up request must not queue behind leftover work from the cancelled one.
        sw.Restart();
        double firstMs = -1;
        await service.GenerateSpeechStreamingAsync(
            "Second request starts right away.", "am_adam", 1.0, CudaDeviceService.AutoDeviceId, "float32",
            onSegmentAsync: _ => { if (firstMs < 0) firstMs = sw.Elapsed.TotalMilliseconds; return Task.CompletedTask; });
        _output.WriteLine($"follow-up request: first audio at {firstMs:F0} ms");
        Assert.True(firstMs < 2500, $"follow-up first audio took {firstMs:F0} ms");
    }

    private async Task<(double firstMs, double totalMs, int segments, double audio)> RunOnce(LocalTtsService service, int deviceId, string precision, int run)
    {
        var sw = Stopwatch.StartNew();
        double firstMs = -1;
        long samples = 0;
        int segments = 0;
        var segLog = new System.Text.StringBuilder();

        await service.GenerateSpeechStreamingAsync(
            SampleText, "am_adam", 1.28, deviceId, precision,
            onSegmentAsync: s =>
            {
                var t = sw.Elapsed.TotalMilliseconds;
                if (firstMs < 0) firstMs = t;
                samples += s.Length;
                segments++;
                segLog.Append($" {t:F0}ms/{s.Length / 24000.0:F1}s");
                return Task.CompletedTask;
            });

        var total = sw.Elapsed.TotalMilliseconds;
        var audio = samples / 24000.0;
        _output.WriteLine($"  run{run}: first audio {firstMs:F0} ms, total {total:F0} ms, segments={segments}, audio={audio:F2}s, {audio / (total / 1000):F1}x realtime [{service.State}]");
        _output.WriteLine($"    segments (ready-at/duration):{segLog}");
        return (firstMs, total, segments, audio);
    }

    private static void EnsureModelsPresent()
    {
        var dir = AppContext.BaseDirectory;
        var root = FindRepoRoot(dir);
        if (root == null) return;

        foreach (var name in new[] { "kokoro.onnx", "kokoro-fp16.onnx" })
        {
            var dst = Path.Combine(dir, name);
            var src = Path.Combine(root, name);
            if (!File.Exists(dst) && File.Exists(src))
            {
                try { File.CreateSymbolicLink(dst, src); }
                catch { File.Copy(src, dst); }
            }
        }
    }

    private static string? FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "DynamiteTts.sln"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }
}
