using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Native;
using NAudio.Wave;

namespace DynamiteTts.Services;

public enum PhononEngineState
{
    NotInitialized,
    /// <summary>dynamite_phonon.dll is not next to the executable (a build without the Rust shim).</summary>
    RuntimeMissing,
    Downloading,
    Loading,
    Ready,
    Failed
}

/// <summary>
/// In-process speech through Gradium's Phonon (github.com/gradium-ai/xn-ptts) via the Rust shim in
/// <c>windows/native/phonon</c>. Phonon is a 31M/40M-parameter flow-matching model that streams
/// 80 ms frames as it generates them, so the first audio is ready in tens of milliseconds and the
/// whole utterance renders at about 10x realtime on a few CPU threads. Measured on a Ryzen AI 9
/// HX 370: load 0.06 s, first audio 20-80 ms, 9-11x realtime with 3-4 threads.
///
/// The model is bundled next to the executable (<c>models/phonon-31m-en</c>) or downloaded once
/// into <see cref="ModelCacheDirectory"/>; catalogue voices (250 KB each) are fetched on first
/// use. A custom checkpoint folder (for the gated multilingual 40M model) can be pointed to in
/// Settings.
/// </summary>
public sealed class PhononTtsService : IDisposable
{
    public const int SampleRate = 24000;
    public const string DefaultModelId = "phonon-31m-en";

    /// <summary>The commit the ptts 0.6.0 runtime itself pins for its default model.</summary>
    public const string ModelRevision = "8c64b55ec301728e2d1695bfea87a08a267d0c2d";
    private const string ModelRepo = "https://huggingface.co/gradium/phonon-31m-en/resolve/" + ModelRevision + "/";

    /// <summary>The commit the runtime pins for its voice catalogue; the embedded voices.jsonl is this snapshot.</summary>
    public const string VoiceCatalogRevision = "da6c9ec0d47be8df5c87c730a3aa97187f3229c3";
    private const string VoiceRepo = "https://huggingface.co/gradium/phonon-voices/resolve/" + VoiceCatalogRevision + "/";

    public sealed record ModelFile(string Name, long Size, string Sha256);

    /// <summary>The checkpoint files the shim needs, with the hashes of the pinned revision.</summary>
    public static readonly IReadOnlyList<ModelFile> ModelFiles = new[]
    {
        new ModelFile("config.json", 2_851, "7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06"),
        new ModelFile("tokenizer.json", 277_967, "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936"),
        new ModelFile("model.q8.gguf", 64_512_352, "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae"),
        new ModelFile("default-voice.safetensors", 256_136, "f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18")
    };

    public static long TotalDownloadBytes => ModelFiles.Sum(f => f.Size);

    public static string ModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamiteTts", "models");

    public static string ModelCacheDirectory => Path.Combine(ModelsRoot, DefaultModelId);
    public static string VoiceCacheDirectory => Path.Combine(ModelsRoot, "phonon-voices");

    /// <summary>
    /// CPU threads for the engine. Phonon is one autoregressive stream, which saturates at a few
    /// threads: measured on a 12-core Ryzen AI 9 HX 370, 3-4 threads gave 9-11x realtime and the
    /// default (one per logical core) only 5x, because the threads fight over the same small matrices.
    /// </summary>
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    /// <summary>Maximum magnitude of the duration_delta condition; larger values make speech unreliable.</summary>
    public const double MaxDurationDelta = 0.8;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private IntPtr _handle;
    private string _modelDir = string.Empty;
    private string _language = string.Empty;
    private float _durationDelta = float.NaN;
    private readonly HashSet<string> _registeredVoices = new(StringComparer.OrdinalIgnoreCase);
    private Task? _initTask;
    private bool _isDisposed;
    private PhononEngineState _state = PhononEngineState.NotInitialized;
    private string _description = "Not initialized";
    private double _downloadProgress;

    public event Action<string>? StatusChanged;

    public PhononEngineState State { get { lock (_lock) return _state; } }
    public string Description { get { lock (_lock) return _description; } }
    public bool IsLoaded { get { lock (_lock) return _handle != IntPtr.Zero; } }
    public string LoadedModelDirectory { get { lock (_lock) return _modelDir; } }
    public double DownloadProgress { get { lock (_lock) return _downloadProgress; } }
    public static bool IsRuntimePresent() => PhononNative.IsLibraryPresent();

    private void SetState(PhononEngineState state, string description)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != state;
            if (!changed && _description == description) return;
            _state = state;
            _description = description;
        }
        if (changed) AppLog.Info($"phonon: {state}: {description}");
        try { StatusChanged?.Invoke(description); } catch (Exception ex) { AppLog.Warn("Phonon StatusChanged handler failed", ex); }
    }

    // ------------------------------------------------------------------ speed

    /// <summary>
    /// Maps the app's speed multiplier onto Phonon's duration_delta condition (negative = faster).
    /// Measured on phonon-31m-en: -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x, which fits
    /// speed ≈ exp(-0.5·delta). Clamped to ±<see cref="MaxDurationDelta"/>, i.e. about 0.67x-1.5x;
    /// the model does not speak reliably beyond that.
    /// </summary>
    public static float DurationDeltaForSpeed(double speed)
    {
        if (double.IsNaN(speed) || speed <= 0) return 0f;
        var delta = -2.0 * Math.Log(speed);
        return (float)Math.Clamp(delta, -MaxDurationDelta, MaxDurationDelta);
    }

    // ------------------------------------------------------------------ model files

    /// <summary>
    /// The checkpoint folder to load: <paramref name="customDir"/> when it holds a config.json,
    /// otherwise the bundled copy next to the executable, the working directory, or the per-user cache.
    /// Null when the default model has not been downloaded yet.
    /// </summary>
    public static string? ResolveModelDirectory(string? customDir)
    {
        if (!string.IsNullOrWhiteSpace(customDir))
        {
            var dir = customDir.Trim();
            if (IsCheckpoint(dir)) return dir;
            throw new DirectoryNotFoundException($"The Phonon model folder '{dir}' has no config.json and weights (model.q8.gguf or model.safetensors).");
        }

        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var candidate = Path.Combine(root, "models", DefaultModelId);
            if (IsDefaultCheckpointComplete(candidate)) return candidate;
        }
        return IsDefaultCheckpointComplete(ModelCacheDirectory) ? ModelCacheDirectory : null;
    }

    public static bool IsCheckpoint(string dir) =>
        Directory.Exists(dir) &&
        File.Exists(Path.Combine(dir, "config.json")) &&
        File.Exists(Path.Combine(dir, "tokenizer.json")) &&
        (File.Exists(Path.Combine(dir, "model.q8.gguf")) || File.Exists(Path.Combine(dir, "model.safetensors")));

    private static bool IsDefaultCheckpointComplete(string dir) =>
        ModelFiles.All(f => File.Exists(Path.Combine(dir, f.Name)) && new FileInfo(Path.Combine(dir, f.Name)).Length == f.Size);

    /// <summary>Downloads the default checkpoint into the cache (sha256-verified), skipping files already there.</summary>
    public async Task<string> EnsureDefaultModelAsync(CancellationToken ct = default)
    {
        var dir = ModelCacheDirectory;
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0");
        long done = 0;
        foreach (var file in ModelFiles)
        {
            var target = Path.Combine(dir, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size && await HashMatchesAsync(target, file.Sha256, ct))
            {
                done += file.Size;
                ReportDownload(done);
                continue;
            }
            var baseDone = done;
            await DownloadAsync(http, ModelRepo + file.Name, target, file.Size, file.Sha256, received => ReportDownload(baseDone + received), ct);
            done += file.Size;
            ReportDownload(done);
        }
        return dir;
    }

    private void ReportDownload(long bytes)
    {
        var fraction = Math.Clamp((double)bytes / TotalDownloadBytes, 0, 1);
        lock (_lock) _downloadProgress = fraction;
        if (State == PhononEngineState.Downloading)
            SetState(PhononEngineState.Downloading, $"Downloading the Phonon model ({fraction:P0} of {TotalDownloadBytes / 1_000_000} MB)…");
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target, long expectedSize, string? sha256, Action<long> onProgress, CancellationToken ct)
    {
        var tmp = target + ".tmp";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long received = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                onProgress(received);
            }
        }
        if (expectedSize > 0 && new FileInfo(tmp).Length != expectedSize)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"{Path.GetFileName(target)} downloaded with the wrong size.");
        }
        if (sha256 != null && !await HashMatchesAsync(tmp, sha256, ct))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"{Path.GetFileName(target)} failed its checksum.");
        }
        File.Move(tmp, target, overwrite: true);
    }

    private static async Task<bool> HashMatchesAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The local file of a catalogue voice, downloading it (once) when missing.</summary>
    public async Task<string> EnsureVoiceFileAsync(PhononVoice voice, CancellationToken ct = default)
    {
        Directory.CreateDirectory(VoiceCacheDirectory);
        var target = Path.Combine(VoiceCacheDirectory, voice.VoiceId + ".safetensors");
        if (File.Exists(target) && new FileInfo(target).Length > 1_000) return target;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0");
        AppLog.Info($"phonon: downloading voice {voice.Name}");
        await DownloadAsync(http, VoiceRepo + voice.FileName, target, 0, null, _ => { }, ct);
        return target;
    }

    // ------------------------------------------------------------------ engine lifecycle

    /// <summary>
    /// Loads the engine for <paramref name="settings"/> (downloading the default model first when it
    /// is not on disk). Returns the in-flight task when a load is already running; returns at once
    /// when the same model and language are loaded.
    /// </summary>
    public Task InitializeAsync(AppSettings settings, CancellationToken ct = default) =>
        InitializeAsync(settings.PhononModelDir, settings.PhononLanguage, ct);

    public Task InitializeAsync(string? customModelDir, string language, CancellationToken ct = default)
    {
        if (_isDisposed) return Task.CompletedTask;
        language = NormalizeLanguage(language);
        lock (_lock)
        {
            if (_initTask != null) return _initTask;
            if (_handle != IntPtr.Zero && SameModel(customModelDir) && string.Equals(_language, language, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            _initTask = Task.Run(() => InitializeCoreAsync(customModelDir, language, ct), ct);
            return _initTask.ContinueWith(t =>
            {
                lock (_lock) _initTask = null;
                if (t.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.Exception!.GetBaseException()).Throw();
            }, TaskScheduler.Default);
        }
    }

    private bool SameModel(string? customModelDir)
    {
        try
        {
            var wanted = ResolveModelDirectory(customModelDir);
            return wanted != null && string.Equals(Path.GetFullPath(wanted), Path.GetFullPath(_modelDir), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string NormalizeLanguage(string? language)
    {
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        return lang.Length == 0 ? "en" : lang;
    }

    private async Task InitializeCoreAsync(string? customModelDir, string language, CancellationToken ct)
    {
        if (!PhononNative.IsLibraryPresent())
        {
            SetState(PhononEngineState.RuntimeMissing, $"{PhononNative.LibraryFileName} is missing next to the app. Build it with `cargo build --release` in windows/native/phonon, or use a release build.");
            throw new DllNotFoundException($"{PhononNative.LibraryFileName} was not found in {AppContext.BaseDirectory}.");
        }

        string? dir;
        try
        {
            dir = ResolveModelDirectory(customModelDir);
        }
        catch (Exception ex)
        {
            SetState(PhononEngineState.Failed, ex.Message);
            throw;
        }

        if (dir == null)
        {
            SetState(PhononEngineState.Downloading, $"Downloading the Phonon model ({TotalDownloadBytes / 1_000_000} MB)…");
            try
            {
                dir = await EnsureDefaultModelAsync(ct);
            }
            catch (Exception ex)
            {
                SetState(PhononEngineState.Failed, $"Phonon model download failed: {Shorten(ex.Message)}");
                throw;
            }
        }

        SetState(PhononEngineState.Loading, $"Loading Phonon from {dir}…");
        var sw = Stopwatch.StartNew();
        await _engineGate.WaitAsync(ct);
        try
        {
            var handle = PhononNative.dp_new(dir, language, (uint)Math.Max(1, Threads));
            if (handle == IntPtr.Zero)
            {
                var error = PhononNative.LastError(IntPtr.Zero);
                SetState(PhononEngineState.Failed, $"Phonon failed to load: {Shorten(error)}");
                throw new InvalidOperationException($"Phonon failed to load {dir}: {error}");
            }

            IntPtr old;
            lock (_lock)
            {
                old = _handle;
                _handle = handle;
                _modelDir = dir;
                _language = language;
                _durationDelta = float.NaN;
                _registeredVoices.Clear();
                foreach (var v in PhononNative.Voices(handle)) _registeredVoices.Add(v);
            }
            if (old != IntPtr.Zero) PhononNative.dp_free(old);

            // Prime the model so the first real request pays no first-call cost.
            try
            {
                PhononNative.dp_speak(handle, "Ready.", null, -1f, WarmUpCallbackPointer(), IntPtr.Zero, out _);
            }
            catch (Exception ex) { AppLog.Warn("Phonon warm-up failed", ex); }
        }
        finally
        {
            _engineGate.Release();
        }

        var modelName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        AppLog.Info($"phonon: loaded {modelName} (runtime {PhononNative.RuntimeVersion()}, {Threads} threads, lang {language}) in {sw.ElapsedMilliseconds} ms");
        SetState(PhononEngineState.Ready, $"{modelName} on the CPU ({Threads} threads, language {language})");
    }

    private static unsafe IntPtr WarmUpCallbackPointer()
    {
        delegate* unmanaged[Cdecl]<float*, nuint, void*, int> p = &OnWarmUpFrame;
        return (IntPtr)p;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnWarmUpFrame(float* pcm, nuint n, void* user) => 1;

    // ------------------------------------------------------------------ synthesis

    /// <summary>
    /// Streams <paramref name="chunks"/> through Phonon, invoking <paramref name="onSegmentAsync"/>
    /// with each decoded block of 24 kHz mono samples as soon as the engine produces it. Returns when
    /// every block has been handed over. Cancellation stops generation within one frame.
    /// </summary>
    public async Task GenerateSpeechStreamingAsync(
        IReadOnlyList<string> chunks,
        string? voiceName,
        double speed,
        string? customModelDir,
        string language,
        Func<float[], Task> onSegmentAsync,
        CancellationToken cancellationToken = default)
    {
        if (chunks == null || chunks.All(string.IsNullOrWhiteSpace)) return;

        await InitializeAsync(customModelDir, language, cancellationToken);
        var voice = await ResolveVoiceAsync(voiceName, cancellationToken);
        var delta = DurationDeltaForSpeed(speed);

        var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var consumer = Task.Run(async () =>
        {
            await foreach (var samples in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (samples.Length > 0) await onSegmentAsync(samples);
            }
        }, cancellationToken);

        var producer = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var chunk in chunks)
                {
                    if (string.IsNullOrWhiteSpace(chunk)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    Speak(chunk.Trim(), voice, delta, channel.Writer, cancellationToken);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Task.WhenAll(producer, consumer);
    }

    /// <summary>Renders the whole text to a 16-bit PCM WAV byte array (non-streaming fallback).</summary>
    public async Task<byte[]> GenerateSpeechWavAsync(
        string text, string? voiceName, double speed, string? customModelDir, string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();

        var all = new List<float>();
        await GenerateSpeechStreamingAsync(new[] { text }, voiceName, speed, customModelDir, language,
            samples => { all.AddRange(samples); return Task.CompletedTask; }, cancellationToken);
        if (all.Count == 0) return Array.Empty<byte>();

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

    /// <summary>
    /// The voice name to pass to the engine: null for the checkpoint's default, a catalogue voice
    /// (downloaded and registered on first use), or a voice the loaded checkpoint itself ships.
    /// </summary>
    private async Task<string?> ResolveVoiceAsync(string? voiceName, CancellationToken ct)
    {
        var requested = (voiceName ?? string.Empty).Trim();
        if (requested.Length == 0 || string.Equals(requested, PhononVoice.DefaultId, StringComparison.OrdinalIgnoreCase))
            return null;

        lock (_lock)
        {
            if (_registeredVoices.TryGetValue(requested, out var exact)) return exact;
        }

        var catalogVoice = PhononVoice.Find(requested);
        if (catalogVoice == null || catalogVoice.IsDefault)
        {
            AppLog.Warn($"phonon: unknown voice '{requested}', using the model's default voice");
            return null;
        }

        lock (_lock)
        {
            if (_registeredVoices.TryGetValue(catalogVoice.Name, out var known)) return known;
        }

        string file;
        try
        {
            file = await EnsureVoiceFileAsync(catalogVoice, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"phonon: could not download voice {catalogVoice.Name}; using the default voice", ex);
            return null;
        }

        await _engineGate.WaitAsync(ct);
        try
        {
            IntPtr handle;
            lock (_lock) handle = _handle;
            if (handle == IntPtr.Zero) return null;
            if (PhononNative.dp_add_voice(handle, catalogVoice.Name, file) == 0)
            {
                AppLog.Warn($"phonon: registering voice {catalogVoice.Name} failed: {PhononNative.LastError(handle)}");
                return null;
            }
            lock (_lock) _registeredVoices.Add(catalogVoice.Name);
            return catalogVoice.Name;
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>Voices the loaded engine can speak right now (checkpoint voices plus registered catalogue voices).</summary>
    public IReadOnlyList<string> LoadedVoices { get { lock (_lock) return _registeredVoices.ToArray(); } }

    private sealed class SpeakContext
    {
        public ChannelWriter<float[]> Writer = null!;
        public CancellationToken Token;
        public Exception? Error;
        public int Blocks;
        public long Samples;
    }

    private void Speak(string text, string? voice, float delta, ChannelWriter<float[]> writer, CancellationToken ct)
    {
        _engineGate.Wait(ct);
        var context = new SpeakContext { Writer = writer, Token = ct };
        var gc = GCHandle.Alloc(context);
        try
        {
            IntPtr handle;
            lock (_lock) handle = _handle;
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The Phonon engine is not loaded.");

            if (float.IsNaN(_durationDelta) || Math.Abs(_durationDelta - delta) > 1e-4f)
            {
                if (PhononNative.dp_set_duration_delta(handle, delta) == 0)
                    AppLog.Warn($"phonon: duration_delta {delta:F2} rejected: {PhononNative.LastError(handle)}");
                else
                    _durationDelta = delta;
            }

            var sw = Stopwatch.StartNew();
            int ok;
            PhononNative.DpResult result;
            unsafe
            {
                delegate* unmanaged[Cdecl]<float*, nuint, void*, int> cb = &OnFrame;
                ok = PhononNative.dp_speak(handle, text, voice, -1f, (IntPtr)cb, GCHandle.ToIntPtr(gc), out result);
            }

            ct.ThrowIfCancellationRequested();
            if (context.Error != null) throw context.Error;
            if (ok == 0) throw new InvalidOperationException($"Phonon synthesis failed: {PhononNative.LastError(handle)}");
            AppLog.Info($"phonon: {text.Length} chars -> {result.Samples / (double)SampleRate:F1}s audio in {context.Blocks} blocks, first audio {result.TtfaMs:F0} ms, total {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            gc.Free();
            _engineGate.Release();
        }
    }

    /// <summary>
    /// Runs on the thread inside dp_speak. Copies the block out and hands it to the consumer, blocking
    /// while the bounded channel is full (which pauses generation: back-pressure all the way down).
    /// Must never throw across the native frame: cancellation and errors become "stop" (return 0).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnFrame(float* pcm, nuint n, void* user)
    {
        var context = (SpeakContext)GCHandle.FromIntPtr((IntPtr)user).Target!;
        try
        {
            if (context.Token.IsCancellationRequested) return 0;
            var count = checked((int)n);
            var samples = new float[count];
            new ReadOnlySpan<float>(pcm, count).CopyTo(samples);
            context.Blocks++;
            context.Samples += count;
            var write = context.Writer.WriteAsync(samples, context.Token);
            if (!write.IsCompletedSuccessfully) write.AsTask().GetAwaiter().GetResult();
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            context.Error = ex;
            return 0;
        }
    }

    private static string Shorten(string message)
    {
        var line = (message ?? string.Empty).Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _engineGate.Wait();
        try
        {
            IntPtr handle;
            lock (_lock)
            {
                handle = _handle;
                _handle = IntPtr.Zero;
            }
            if (handle != IntPtr.Zero) PhononNative.dp_free(handle);
        }
        finally
        {
            _engineGate.Release();
            _engineGate.Dispose();
        }
    }
}
