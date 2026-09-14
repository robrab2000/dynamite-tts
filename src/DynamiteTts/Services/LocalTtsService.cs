using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace DynamiteTts.Services;

public enum LocalEngineState
{
    NotInitialized,
    Loading,
    /// <summary>CPU is the only backend (by choice, or no usable NVIDIA GPU).</summary>
    CpuOnly,
    /// <summary>CUDA runtime DLLs are being downloaded; CPU serves meanwhile.</summary>
    GpuRuntimeDownloading,
    /// <summary>CUDA runtime download failed; CPU serves.</summary>
    GpuRuntimeUnavailable,
    /// <summary>GPU is usable but its session is not loaded (unloaded after idling, or never used yet).</summary>
    GpuIdle,
    /// <summary>GPU session is being created/warmed; CPU serves the segments until it is ready.</summary>
    GpuLoading,
    GpuActive,
    /// <summary>GPU session failed to create or run; CPU serves until the next re-initialization.</summary>
    GpuFailed
}

/// <summary>
/// In-process Kokoro synthesis on top of ONNX Runtime.
///
/// Drives the ONNX session directly (tokenizer and voices still come from KokoroSharp) so that:
///  - text is split into short, sentence-sized segments and each one is handed to playback the
///    moment it is ready (KokoroSharp's default segmentation renders everything after the first
///    segment as one 510-token block, which stalls playback for many seconds);
///  - a Stop / new request cancels the in-flight inference instead of leaving an orphaned job
///    that the next request has to queue behind;
///  - two sessions can coexist: a CPU session that is always warm, and a CUDA session on the
///    NVIDIA GPU that is created on first use (2-5 s) while the CPU renders the first sentences,
///    then unloaded again after <see cref="GpuIdleTimeout"/> so a tray-resident app does not keep
///    the discrete GPU powered up. Both run the same fp32 graph, so switching mid-utterance is
///    inaudible. Measured on an RTX 5070 Laptop: 32-token segment 0.14 s on CUDA vs 0.7 s on CPU.
/// </summary>
public class LocalTtsService : IDisposable
{
    public const int SampleRate = 24000;
    private const int MaxModelTokens = 510;

    /// <summary>Token budget for the very first segment of an utterance (drives time-to-first-audio).</summary>
    public int FirstSegmentMaxTokens { get; set; } = 32;

    /// <summary>Token budget for follow-up segments on the CPU. Roughly one or two sentences.</summary>
    public int SegmentMaxTokens { get; set; } = 120;

    /// <summary>Token budget for follow-up segments on the GPU, where cost grows faster than linearly with length.</summary>
    public int GpuSegmentMaxTokens { get; set; } = 96;

    /// <summary>Number of CPU threads for intra-op parallelism when running on the CPU provider.</summary>
    public int CpuThreads { get; set; } = DefaultCpuThreads();

    /// <summary>How long the GPU session may sit unused before it is disposed and the GPU released.</summary>
    public TimeSpan GpuIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether initialization may start the ~1 GB CUDA runtime download when it is missing.</summary>
    public bool AutoDownloadGpuRuntime { get; set; } = true;

    private readonly object _lock = new();
    private readonly object _gpuLifecycle = new();
    private readonly CudaRuntimeService _runtime;

    private InferenceSession? _cpuSession;
    private InferenceSession? _gpuSession;
    private Task? _gpuLoadTask;
    private RunOptions? _activeRun;
    private Timer? _idleTimer;
    private string _tokenInputName = "tokens";
    private string _modelPath = string.Empty;
    private string _modelPrecision = "float32";
    private bool _isInitializing;
    private bool _isDisposed;
    private Task? _initTask;

    private bool _useGpu = true;
    private int _deviceId = CudaDeviceService.AutoDeviceId;
    private string _precision = "float32";
    private int _cudaOrdinal = -1;
    private string _gpuName = string.Empty;
    private string? _gpuFailure;
    private DateTime _gpuLastUsed = DateTime.MinValue;
    private int _activeUtterances;
    private int _lastReportedPercent = -1;

    private LocalEngineState _state = LocalEngineState.NotInitialized;
    private string _description = "Not initialized";

    /// <summary>Raised (from a background thread) whenever the state or description changes.</summary>
    public event Action<string>? StatusChanged;

    public LocalTtsService() : this(new CudaRuntimeService()) { }

    public LocalTtsService(CudaRuntimeService runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.ProgressChanged += OnRuntimeProgress;
    }

    public LocalEngineState State { get { lock (_lock) return _state; } }
    public string ActiveDeviceDescription { get { lock (_lock) return _description; } }
    public bool IsInitialized { get { lock (_lock) return _cpuSession != null; } }
    public bool IsGpuActive { get { lock (_lock) return _gpuSession != null; } }
    public bool IsGpuConfigured { get { lock (_lock) return _cudaOrdinal >= 0; } }
    public string GpuName { get { lock (_lock) return _gpuName; } }
    public int CurrentDeviceId { get { lock (_lock) return _deviceId; } }
    public string CurrentPrecision { get { lock (_lock) return _precision; } }
    public double GpuRuntimeProgress => _runtime.Progress;
    public static string CpuDescriptionFor(int threads, string precision) => $"CPU ({threads} threads, {precision})";

    private static int DefaultCpuThreads()
    {
        // One thread per physical core. Measured on a Ryzen AI 9 HX 370 (12c/24t): 12 threads ≈ 3.3x
        // realtime, 8 ≈ 3.0x, 24 (all logical) ≈ 1.5x because SMT siblings fight over the same FPUs.
        var logical = Environment.ProcessorCount;
        return Math.Clamp(logical / 2, 2, 16);
    }

    private void SetState(LocalEngineState state, string description)
    {
        bool stateChanged;
        lock (_lock)
        {
            if (_state == state && _description == description) return;
            stateChanged = _state != state;
            _state = state;
            _description = description;
        }
        if (stateChanged) AppLog.Info($"engine: {state}: {description}");
        StatusChanged?.Invoke(description);
    }

    // ------------------------------------------------------------------ initialization

    /// <summary>
    /// Loads the CPU session (always) and decides whether the GPU may be used. Returns the in-flight
    /// task when an initialization is already running; returns immediately when nothing changed.
    /// </summary>
    public Task InitializeAsync(
        bool useGpu = true,
        int deviceId = CudaDeviceService.AutoDeviceId,
        string precision = "float32",
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return Task.CompletedTask;

        // Sentinel for the informational NPU list entry — map back to Auto
        if (deviceId == CudaDeviceService.NpuUnavailableDeviceId)
            deviceId = CudaDeviceService.AutoDeviceId;
        precision = string.IsNullOrWhiteSpace(precision) ? "float32" : precision;

        Task init;
        lock (_lock)
        {
            if (_cpuSession != null &&
                _useGpu == useGpu &&
                _deviceId == deviceId &&
                string.Equals(_precision, precision, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            if (_isInitializing && _initTask != null)
                return _initTask;

            _isInitializing = true;
            _useGpu = useGpu;
            _deviceId = deviceId;
            _precision = precision;
            init = Task.Run(() => InitializeCore(useGpu, deviceId, precision, cancellationToken), cancellationToken);
            _initTask = init;
        }

        return init.ContinueWith(t =>
        {
            lock (_lock)
            {
                _isInitializing = false;
                _initTask = null;
            }
            if (t.IsFaulted)
            {
                var error = t.Exception!.GetBaseException();
                AppLog.Error("Local engine initialization failed", error);
                SetState(LocalEngineState.NotInitialized, $"Local Kokoro engine failed to load: {Shorten(error.Message)}");
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }, TaskScheduler.Default);
    }

    private void InitializeCore(bool useGpu, int deviceId, string precision, CancellationToken ct)
    {
        SetState(LocalEngineState.Loading, "Loading the Kokoro model…");

        // fp16 buys nothing on either backend here (CPU: slightly slower; CUDA: within noise) and
        // loses a little quality, so the fp32 graph is used whenever it is on disk.
        var modelPrecision = precision;
        string modelPath;
        if (TryFindLocalModel("float32", out var fp32Path))
        {
            modelPath = fp32Path;
            modelPrecision = "float32";
        }
        else
        {
            modelPath = ResolveModelPathAsync(precision, ct).GetAwaiter().GetResult();
        }

        // Warm the tokenizer (first call loads the phoneme dictionaries and takes ~1s).
        try { Tokenizer.Tokenize("Warming up.", "en-us", true); } catch { }

        var cpu = new InferenceSession(modelPath, CreateCpuSessionOptions());
        WarmUp(cpu);
        var tokenName = cpu.InputMetadata.Keys.FirstOrDefault(k => k is "tokens" or "input_ids")
                        ?? cpu.InputMetadata.Keys.First();

        InferenceSession? oldCpu, oldGpu;
        int oldOrdinal;
        lock (_lock)
        {
            oldCpu = _cpuSession;
            oldGpu = _gpuSession;
            oldOrdinal = _cudaOrdinal;
            _cpuSession = cpu;
            _gpuSession = null;
            _gpuFailure = null;
            _tokenInputName = tokenName;
            _modelPath = modelPath;
            _modelPrecision = modelPrecision;
            _cudaOrdinal = -1;
            _gpuName = string.Empty;
        }
        oldCpu?.Dispose();
        if (oldGpu != null)
        {
            lock (_gpuLifecycle)
            {
                oldGpu.Dispose();
                if (oldOrdinal >= 0) CudaDeviceService.ReleasePrimaryContext(oldOrdinal);
            }
        }

        PlanGpu(useGpu, deviceId, CpuDescriptionFor(CpuThreads, modelPrecision), refreshProbe: false);
        StartIdleTimer();
    }

    /// <summary>Decides whether and how the GPU will be used; the CPU session must already be loaded.</summary>
    private void PlanGpu(bool useGpu, int deviceId, string cpuDesc, bool refreshProbe)
    {
        if (!useGpu || deviceId == CudaDeviceService.CpuOnlyDeviceId)
        {
            SetState(LocalEngineState.CpuOnly, deviceId == CudaDeviceService.CpuOnlyDeviceId ? $"{cpuDesc} — GPU disabled in settings" : cpuDesc);
            return;
        }

        var probe = refreshProbe ? CudaDeviceService.Refresh() : CudaDeviceService.Probe();
        var ordinal = CudaDeviceService.ResolveCudaOrdinal(deviceId, probe);
        if (ordinal < 0)
        {
            SetState(LocalEngineState.CpuOnly, $"{cpuDesc} — {probe.Unavailability ?? "no NVIDIA GPU detected"}");
            return;
        }

        var gpuName = probe.Devices.First(d => d.Ordinal == ordinal).Name;
        lock (_lock)
        {
            _cudaOrdinal = ordinal;
            _gpuName = gpuName;
        }

        if (CudaRuntimeService.IsInstalled())
        {
            SetState(LocalEngineState.GpuIdle, $"{gpuName} (CUDA) — loads on first use; the CPU covers the first sentences");
        }
        else if (AutoDownloadGpuRuntime)
        {
            StartRuntimeInstall(gpuName, cpuDesc);
        }
        else
        {
            SetState(LocalEngineState.GpuRuntimeUnavailable, $"{cpuDesc} — CUDA runtime for {gpuName} not downloaded");
        }
    }

    /// <summary>
    /// Re-probes the GPU and retries the runtime download / session load after a failure, without
    /// reloading the CPU session. No-op while the engine is loading or a download is running.
    /// </summary>
    public Task RetryGpuAsync()
    {
        bool useGpu; int deviceId; string cpuDesc;
        lock (_lock)
        {
            if (_isDisposed || _cpuSession == null || _isInitializing ||
                _state is LocalEngineState.GpuRuntimeDownloading or LocalEngineState.GpuLoading or LocalEngineState.GpuActive)
                return Task.CompletedTask;
            _gpuFailure = null;
            useGpu = _useGpu;
            deviceId = _deviceId;
            cpuDesc = CpuDescriptionFor(CpuThreads, _modelPrecision);
        }
        return Task.Run(() => PlanGpu(useGpu, deviceId, cpuDesc, refreshProbe: true));
    }

    private void StartRuntimeInstall(string gpuName, string cpuDesc)
    {
        _lastReportedPercent = -1;
        SetState(LocalEngineState.GpuRuntimeDownloading, DownloadDescription(0));

        _runtime.EnsureInstalledAsync().ContinueWith(t =>
        {
            if (_isDisposed) return;
            if (t.IsFaulted || t.IsCanceled)
            {
                var reason = t.Exception?.GetBaseException().Message ?? "cancelled";
                SetState(LocalEngineState.GpuRuntimeUnavailable, $"{cpuDesc} — CUDA runtime download failed: {Shorten(reason)}");
                return;
            }
            SetState(LocalEngineState.GpuIdle, $"{gpuName} (CUDA) — runtime installed; loads on first use");
        }, TaskScheduler.Default);
    }

    private string DownloadDescription(double fraction) =>
        $"Downloading the NVIDIA CUDA runtime ({fraction:P0} of {CudaRuntimeService.TotalDownloadBytes / 1_000_000} MB) — speech runs on the CPU until it finishes";

    private void OnRuntimeProgress(double fraction)
    {
        var percent = (int)(fraction * 100);
        if (percent == _lastReportedPercent) return;
        _lastReportedPercent = percent;
        if (State == LocalEngineState.GpuRuntimeDownloading)
            SetState(LocalEngineState.GpuRuntimeDownloading, DownloadDescription(fraction));
    }

    private SessionOptions CreateCpuSessionOptions()
    {
        return new SessionOptions
        {
            IntraOpNumThreads = CpuThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = true
        };
    }

    private static SessionOptions CreateCudaSessionOptions(int ordinal)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // A handful of shape/alignment ops fall back to the CPU inside the CUDA session; they are tiny.
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1
        };

        var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = ordinal.ToString(),
            // EXHAUSTIVE autotunes every new conv shape (10-130 s stalls on Blackwell with ORT 1.22);
            // HEURISTIC was measured equally fast at steady state and has no per-shape penalty.
            ["cudnn_conv_algo_search"] = "HEURISTIC",
            ["cudnn_conv_use_max_workspace"] = "1",
            ["arena_extend_strategy"] = "kSameAsRequested"
        });
        options.AppendExecutionProvider_CUDA(cuda);
        return options;
    }

    private void WarmUp(InferenceSession session, bool thorough = false)
    {
        var voice = ResolveVoice("af_heart");
        var lang = KokoroLangCodeHelper.GetLangCode(voice);
        var tokenName = session.InputMetadata.Keys.FirstOrDefault(k => k is "tokens" or "input_ids")
                        ?? session.InputMetadata.Keys.First();

        var texts = thorough
            ? new[] { "Ready.", "This longer sentence primes the kernels for a typical segment of speech, so the first real request is fast." }
            : new[] { "Ready." };

        foreach (var text in texts)
        {
            var tokens = Tokenizer.Tokenize(text, lang, true);
            if (tokens.Length == 0) tokens = new[] { 16, 43, 16 };
            using var results = session.Run(BuildInputs(tokenName, tokens, voice.Features, 1.0f));
            _ = results[0].AsTensor<float>().Length;
        }
    }

    // ------------------------------------------------------------------ GPU session lifecycle

    /// <summary>
    /// Picks the session for the next segment: the GPU session when it is resident, otherwise the
    /// CPU session (kicking off the GPU load in the background if the GPU is available).
    /// </summary>
    private (InferenceSession session, bool onGpu) AcquireSession()
    {
        lock (_lock)
        {
            if (_gpuSession != null)
            {
                _gpuLastUsed = DateTime.UtcNow;
                return (_gpuSession, true);
            }

            if (_cpuSession == null)
                throw new InvalidOperationException("Local Kokoro TTS engine is not initialized.");

            if (_useGpu && _cudaOrdinal >= 0 && _gpuFailure == null && _gpuLoadTask == null &&
                _state is LocalEngineState.GpuIdle or LocalEngineState.GpuActive &&
                CudaRuntimeService.IsInstalled())
            {
                _gpuLoadTask = Task.Run(LoadGpuSession);
            }

            return (_cpuSession, false);
        }
    }

    private void LoadGpuSession()
    {
        int ordinal;
        string modelPath, gpuName;
        lock (_lock)
        {
            ordinal = _cudaOrdinal;
            modelPath = _modelPath;
            gpuName = _gpuName;
        }

        SetState(LocalEngineState.GpuLoading, $"{gpuName} (CUDA) loading — the CPU covers the first sentences");
        var sw = Stopwatch.StartNew();
        InferenceSession? session = null;
        try
        {
            lock (_gpuLifecycle)
            {
                CudaRuntimeService.RegisterDllDirectory();
                session = new InferenceSession(modelPath, CreateCudaSessionOptions(ordinal));
                WarmUp(session, thorough: true);
            }

            var keep = false;
            lock (_lock)
            {
                if (!_isDisposed && _cudaOrdinal == ordinal && _gpuSession == null)
                {
                    _gpuSession = session;
                    _gpuLastUsed = DateTime.UtcNow;
                    keep = true;
                }
            }

            if (!keep)
            {
                session.Dispose();
                return;
            }

            AppLog.Info($"CUDA session ready on {gpuName} in {sw.ElapsedMilliseconds} ms");
            SetState(LocalEngineState.GpuActive, $"{gpuName} (CUDA)");
        }
        catch (Exception ex)
        {
            session?.Dispose();
            AppLog.Warn($"CUDA session failed on {gpuName}", ex);
            lock (_lock) _gpuFailure = ex.Message;
            SetState(LocalEngineState.GpuFailed, $"{CpuDescriptionFor(CpuThreads, _modelPrecision)} — {gpuName} failed: {Shorten(ex.Message)}");
        }
        finally
        {
            lock (_lock) _gpuLoadTask = null;
        }
    }

    /// <summary>Waits for a GPU load that is in progress (tests and diagnostics).</summary>
    public Task WaitForGpuLoadAsync()
    {
        Task? t;
        lock (_lock) t = _gpuLoadTask;
        return t ?? Task.CompletedTask;
    }

    private void StartIdleTimer()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _idleTimer ??= new Timer(_ => UnloadIdleGpu(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Disposes the GPU session when no utterance is running and it has been idle for longer than
    /// <see cref="GpuIdleTimeout"/>; releases the device's primary context so the GPU can power down.
    /// Returns true when a session was unloaded.
    /// </summary>
    public bool UnloadIdleGpu()
    {
        InferenceSession? session;
        int ordinal;
        string gpuName;
        lock (_lock)
        {
            if (_gpuSession == null || _activeUtterances > 0) return false;
            if (DateTime.UtcNow - _gpuLastUsed < GpuIdleTimeout) return false;
            session = _gpuSession;
            _gpuSession = null;
            ordinal = _cudaOrdinal;
            gpuName = _gpuName;
        }

        lock (_gpuLifecycle)
        {
            session.Dispose();
            CudaDeviceService.ReleasePrimaryContext(ordinal);
        }

        AppLog.Info($"CUDA session on {gpuName} unloaded after idling");
        SetState(LocalEngineState.GpuIdle, $"{gpuName} (CUDA) — idle, unloaded to let the GPU sleep; reloads on next use");
        return true;
    }

    private static string Shorten(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    // ------------------------------------------------------------------ model file resolution

    private static readonly Dictionary<string, string> ModelFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["float32"] = "kokoro.onnx",
        ["float16"] = "kokoro-fp16.onnx"
    };

    private const string ModelDownloadBase = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/";

    public static string ModelCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamiteTts", "models");

    /// <summary>Looks for the model next to the executable, in the working directory, or in the per-user cache.</summary>
    public static bool TryFindLocalModel(string precision, out string path)
    {
        if (!ModelFileNames.TryGetValue(precision ?? "float32", out var fileName))
            fileName = ModelFileNames["float32"];

        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), ModelCacheDirectory })
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 1_000_000)
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Finds the ONNX file for the requested precision next to the executable, in the working directory,
    /// or in the per-user cache; downloads it into the cache when it is missing everywhere.
    /// </summary>
    public static async Task<string> ResolveModelPathAsync(string precision, CancellationToken ct = default)
    {
        if (TryFindLocalModel(precision, out var local))
            return local;

        if (!ModelFileNames.TryGetValue(precision ?? "float32", out var fileName))
            fileName = ModelFileNames["float32"];

        Directory.CreateDirectory(ModelCacheDirectory);
        var target = Path.Combine(ModelCacheDirectory, fileName);
        var tmp = target + ".tmp";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(ModelDownloadBase + fileName, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    // ------------------------------------------------------------------ synthesis

    /// <summary>
    /// Synthesizes <paramref name="text"/> and invokes <paramref name="onSegmentAsync"/> with each
    /// segment's 24 kHz mono float samples as soon as it is rendered. Returns when every segment
    /// has been handed over (playback may still be running). Honors cancellation between and
    /// during inference calls.
    /// </summary>
    public Task GenerateSpeechStreamingAsync(
        string text,
        string voiceName,
        double speed,
        int deviceId,
        string precision,
        Func<float[], Task> onSegmentAsync,
        CancellationToken cancellationToken = default)
    {
        return GenerateSpeechStreamingAsync(new[] { text }, voiceName, speed, deviceId, precision, onSegmentAsync, cancellationToken);
    }

    /// <summary>
    /// Same as the single-text overload but tokenizes the input chunk by chunk, so the first audio
    /// does not wait for a long article to be phonemized in full.
    /// </summary>
    public async Task GenerateSpeechStreamingAsync(
        IReadOnlyList<string> chunks,
        string voiceName,
        double speed,
        int deviceId,
        string precision,
        Func<float[], Task> onSegmentAsync,
        CancellationToken cancellationToken = default)
    {
        if (chunks == null || chunks.All(string.IsNullOrWhiteSpace))
            return;

        await EnsureInitializedAsync(deviceId, precision, cancellationToken);
        var voice = ResolveVoice(voiceName);
        var langCode = KokoroLangCodeHelper.GetLangCode(voice);
        var clampedSpeed = (float)Math.Clamp(speed, 0.5, 2.0);

        // Small bounded hand-off so synthesis can run a little ahead of playback without
        // rendering an entire article up front. Playback applies its own back-pressure.
        var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var consumer = Task.Run(async () =>
        {
            await foreach (var samples in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (samples.Length == 0) continue;
                await onSegmentAsync(samples);
            }
        }, cancellationToken);

        lock (_lock) _activeUtterances++;
        try
        {
            var producer = Task.Run(async () =>
            {
                try
                {
                    var isFirstSegment = true;
                    foreach (var chunk in chunks)
                    {
                        if (string.IsNullOrWhiteSpace(chunk)) continue;
                        cancellationToken.ThrowIfCancellationRequested();

                        var tokens = Tokenizer.Tokenize(chunk.Trim(), langCode, true);
                        var budget = IsGpuActive ? GpuSegmentMaxTokens : SegmentMaxTokens;
                        foreach (var segment in Segment(tokens, isFirstSegment, budget))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var (session, _) = AcquireSession();
                            var samples = Infer(session, segment, voice.Features, clampedSpeed, cancellationToken);
                            samples = TrimAndPad(samples, PauseAfter(segment));
                            isFirstSegment = false;
                            await channel.Writer.WriteAsync(samples, cancellationToken);
                        }
                    }
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    throw;
                }
            }, cancellationToken);

            await Task.WhenAll(producer, consumer);
        }
        finally
        {
            lock (_lock)
            {
                _activeUtterances--;
                if (_gpuSession != null) _gpuLastUsed = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Renders the whole text to a 16-bit PCM WAV byte array (non-streaming).</summary>
    public async Task<byte[]> GenerateSpeechWavAsync(
        string text,
        string voiceName,
        double speed,
        int deviceId = CudaDeviceService.AutoDeviceId,
        string precision = "float32",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        var all = new List<float>();
        await GenerateSpeechStreamingAsync(text, voiceName, speed, deviceId, precision,
            samples => { all.AddRange(samples); return Task.CompletedTask; }, cancellationToken);

        if (all.Count == 0)
            return Array.Empty<byte>();

        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(SampleRate, 16, 1)))
        {
            var buffer = new byte[all.Count * 2];
            for (int i = 0; i < all.Count; i++)
            {
                var s = (short)Math.Clamp(all[i] * 32767f, short.MinValue, short.MaxValue);
                buffer[i * 2] = (byte)(s & 0xFF);
                buffer[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            writer.Write(buffer, 0, buffer.Length);
        }
        return ms.ToArray();
    }

    private async Task EnsureInitializedAsync(int deviceId, string precision, CancellationToken ct)
    {
        if (deviceId == CudaDeviceService.NpuUnavailableDeviceId) deviceId = CudaDeviceService.AutoDeviceId;
        precision = string.IsNullOrWhiteSpace(precision) ? "float32" : precision;

        bool needsInit;
        lock (_lock)
        {
            needsInit = _cpuSession == null ||
                        _deviceId != deviceId ||
                        !string.Equals(_precision, precision, StringComparison.OrdinalIgnoreCase);
        }

        if (needsInit)
            await InitializeAsync(_useGpu, deviceId, precision, ct);

        lock (_lock)
        {
            if (_cpuSession == null)
                throw new InvalidOperationException("Local Kokoro TTS engine could not be initialized.");
        }
    }

    private float[] Infer(InferenceSession session, int[] tokens, float[,,] voiceStyle, float speed, CancellationToken ct)
    {
        if (tokens.Length == 0) return Array.Empty<float>();
        if (tokens.Length > MaxModelTokens) Array.Resize(ref tokens, MaxModelTokens);

        var inputs = BuildInputs(_tokenInputName, tokens, voiceStyle, speed);

        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => { try { runOptions.Terminate = true; } catch { } });

        lock (session)
        {
            ct.ThrowIfCancellationRequested();
            _activeRun = runOptions;
            try
            {
                using var results = session.Run(inputs, session.OutputNames, runOptions);
                return results[0].AsTensor<float>().ToArray();
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            finally
            {
                _activeRun = null;
            }
        }
    }

    private static List<NamedOnnxValue> BuildInputs(string tokenInputName, int[] tokens, float[,,] voiceStyle, float speed)
    {
        var T = tokens.Length;
        var C = voiceStyle.GetLength(2);

        var tokenTensor = new DenseTensor<long>(new[] { 1, T + 2 }); // <start>{tokens}<end> — zeros are the pad/start/end token
        for (int i = 0; i < T; i++)
            tokenTensor[0, i + 1] = tokens[i] >= 0 ? tokens[i] : 4; // [unk] -> '.'

        var styleTensor = new DenseTensor<float>(new[] { 1, C });
        var styleRow = Math.Min(T - 1, voiceStyle.GetLength(0) - 1);
        for (int j = 0; j < C; j++)
            styleTensor[0, j] = voiceStyle[styleRow, 0, j];

        var speedTensor = new DenseTensor<float>(new[] { speed }, new[] { 1 });

        return new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(tokenInputName, tokenTensor),
            NamedOnnxValue.CreateFromTensor("style", styleTensor),
            NamedOnnxValue.CreateFromTensor("speed", speedTensor)
        };
    }

    // ------------------------------------------------------------------ segmentation

    private static readonly Lazy<(int newline, int space, int[] sentenceEnds, int[] clauseEnds)> SegmentTokens = new(() =>
    {
        var v = Tokenizer.Vocab;
        int Get(char c) => v.TryGetValue(c, out var t) ? t : -1;
        return (
            Get('\n'),
            Get(' '),
            new[] { Get('.'), Get('!'), Get('?'), Get(':'), Get(';') }.Where(t => t >= 0).ToArray(),
            new[] { Get(','), Get('—'), Get('-') }.Where(t => t >= 0).ToArray());
    });

    public IEnumerable<int[]> Segment(int[] tokens, bool firstOfUtterance) => Segment(tokens, firstOfUtterance, SegmentMaxTokens);

    /// <summary>
    /// Splits phoneme tokens into segments: a short first segment for fast time-to-first-audio,
    /// then sentence-sized segments. Cuts prefer sentence ends, then clause breaks, then spaces.
    /// Newlines always end a segment.
    /// </summary>
    public IEnumerable<int[]> Segment(int[] tokens, bool firstOfUtterance, int segmentBudget)
    {
        var (nl, space, sentenceEnds, clauseEnds) = SegmentTokens.Value;
        var punctuation = Tokenizer.PunctuationTokens;
        var at = 0;
        var isFirst = firstOfUtterance;

        while (at < tokens.Length)
        {
            if (tokens[at] == space || tokens[at] == nl) { at++; continue; }

            var budget = Math.Clamp(isFirst ? FirstSegmentMaxTokens : segmentBudget, 8, MaxModelTokens);
            var lineEnd = Array.IndexOf(tokens, nl, at);
            var hardEnd = lineEnd >= 0 ? lineEnd : tokens.Length;
            int end;

            if (hardEnd - at <= budget)
            {
                end = hardEnd;
            }
            else
            {
                var window = tokens.AsSpan(at, budget);
                var cut = LastIndexOfAny(window, sentenceEnds);
                // Avoid a tiny first piece when a sentence end sits right at the start of the window
                if (cut < Math.Min(8, budget / 4)) cut = Math.Max(cut, LastIndexOfAny(window, clauseEnds));
                if (cut < Math.Min(8, budget / 4)) cut = Math.Max(cut, window.LastIndexOf(space));
                end = cut >= 0 ? at + cut + 1 : at + budget;

                // Keep trailing punctuation (e.g. closing quotes) with this segment
                while (end < hardEnd && end - at < MaxModelTokens && punctuation.Contains(tokens[end])) end++;
            }

            var stop = end;
            while (stop > at && (tokens[stop - 1] == space)) stop--;
            if (stop > at)
            {
                yield return tokens[at..stop];
                isFirst = false;
            }
            at = end;
        }
    }

    private static int LastIndexOfAny(ReadOnlySpan<int> span, int[] values)
    {
        for (int i = span.Length - 1; i >= 0; i--)
            if (Array.IndexOf(values, span[i]) >= 0) return i;
        return -1;
    }

    private static float PauseAfter(int[] segment)
    {
        if (segment.Length == 0) return 0f;
        var last = segment[^1];
        if (!Tokenizer.TokenToChar.TryGetValue(last, out var c)) return 0.05f;
        return c switch
        {
            '.' or '!' or '?' => 0.35f,
            ':' or ';' => 0.25f,
            ',' => 0.12f,
            '\n' => 0.4f,
            _ => 0.05f
        };
    }

    /// <summary>Trims the model's leading/trailing silence and appends a short pause.</summary>
    private static float[] TrimAndPad(float[] samples, float pauseSeconds)
    {
        if (samples.Length == 0) return samples;

        const float threshold = 0.004f;
        var margin = SampleRate / 50; // 20 ms
        int start = 0, end = samples.Length;
        while (start < end && Math.Abs(samples[start]) < threshold) start++;
        while (end > start && Math.Abs(samples[end - 1]) < threshold) end--;
        start = Math.Max(0, start - margin);
        end = Math.Min(samples.Length, end + margin);
        if (end <= start) return Array.Empty<float>();

        var pause = (int)(pauseSeconds * SampleRate);
        var result = new float[(end - start) + pause];
        Array.Copy(samples, start, result, 0, end - start);
        return result;
    }

    // ------------------------------------------------------------------ voices

    public static KokoroVoice ResolveVoice(string voiceName)
    {
        try
        {
            var clean = voiceName?.Trim() ?? "af_heart";
            clean = clean.ToLowerInvariant() switch
            {
                "coral" => "af_heart",
                "alloy" => "af_sarah",
                "echo" => "am_echo",
                "fable" => "bm_fable",
                "onyx" => "am_fenrir",
                "nova" => "af_sky",
                "shimmer" => "af_bella",
                "ash" => "am_adam",
                "sage" => "af_nicole",
                _ => clean
            };

            var voice = KokoroVoiceManager.GetVoice(clean);
            if (voice != null) return voice;
        }
        catch { }

        return KokoroVoiceManager.GetVoice("af_heart") ?? KokoroVoiceManager.Voices[0];
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _runtime.ProgressChanged -= OnRuntimeProgress;

        try
        {
            var active = _activeRun;
            if (active != null) active.Terminate = true;
        }
        catch { }

        InferenceSession? cpu, gpu;
        int ordinal;
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            cpu = _cpuSession;
            gpu = _gpuSession;
            ordinal = _cudaOrdinal;
            _cpuSession = null;
            _gpuSession = null;
        }

        cpu?.Dispose();
        if (gpu != null)
        {
            lock (_gpuLifecycle)
            {
                gpu.Dispose();
                if (ordinal >= 0) CudaDeviceService.ReleasePrimaryContext(ordinal);
            }
        }
    }
}
