using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Native;

namespace DynamiteTts.Services;

/// <summary>Timings for one speak request, measured from the moment synthesis was requested.</summary>
public sealed record PipelineMetrics(
    string Engine,
    int TextLength,
    int Segments,
    double AudioSeconds,
    double FirstSegmentMs,
    double FirstAudioMs,
    double SynthesisDoneMs,
    double PlaybackDoneMs,
    double UnderrunSeconds,
    bool UsedFallback,
    string? FallbackReason,
    bool Cancelled)
{
    public override string ToString() =>
        $"engine={Engine} chars={TextLength} segments={Segments} audio={AudioSeconds:F1}s " +
        $"firstSegment={FirstSegmentMs:F0}ms firstAudible={FirstAudioMs:F0}ms synthesisDone={SynthesisDoneMs:F0}ms " +
        $"playbackDone={PlaybackDoneMs:F0}ms underrun={UnderrunSeconds:F2}s fallback={UsedFallback}" +
        (FallbackReason != null ? $" ({FallbackReason})" : string.Empty) +
        (Cancelled ? " cancelled" : string.Empty);
}

public class SpeechOrchestrator : IDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ClipboardSelectionService _clipboardService;
    private readonly LemonadeTtsClient _ttsClient;
    private readonly LemonadeChatClient _chatClient;
    private readonly LemonadeDependencyService _dependencyService;
    private readonly LocalTtsService _localTtsService;
    private readonly AudioPlaybackService _audioService;
    private readonly TrayIconHost _trayHost;
    private readonly TrayNotificationService _notificationService;

    private readonly object _lock = new();
    private readonly object _stateLock = new();
    private CancellationTokenSource? _currentCts;
    private string? _lastSpokenText;
    private bool _isDisposed;

    /// <summary>
    /// Raised whenever the pipeline state changes. May be raised from any thread; handlers must not
    /// block (marshal with BeginInvoke). A throwing handler is logged and ignored.
    /// </summary>
    public event Action<TrayIconState>? StateChanged;

    /// <summary>Short user-facing notices such as "No text selected".</summary>
    public event Action<string>? Notice;

    /// <summary>Busy-path status text (e.g. downloading summary model). May fire on any thread.</summary>
    public event Action<string>? ProgressMessage;

    /// <summary>Raised when SpeakMode is toggled or changed.</summary>
    public event Action<SpeakMode>? SpeakModeChanged;

    /// <summary>Raised after every speak request with its timings.</summary>
    public event Action<PipelineMetrics>? PipelineCompleted;

    public PipelineMetrics? LastMetrics { get; private set; }

    public TrayIconState CurrentState { get; private set; } = TrayIconState.Idle;

    public SpeakMode CurrentSpeakMode => _settingsStore.Current.SpeakMode;

    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return (_currentCts != null && !_currentCts.IsCancellationRequested) || _audioService.IsPlaying;
            }
        }
    }

    public SpeechOrchestrator(
        AppSettingsStore settingsStore,
        ClipboardSelectionService clipboardService,
        LemonadeTtsClient ttsClient,
        LemonadeChatClient chatClient,
        LocalTtsService localTtsService,
        AudioPlaybackService audioService,
        TrayIconHost trayHost,
        TrayNotificationService notificationService,
        LemonadeDependencyService? dependencyService = null)
    {
        _settingsStore = settingsStore;
        _clipboardService = clipboardService;
        _ttsClient = ttsClient;
        _chatClient = chatClient;
        _localTtsService = localTtsService;
        _audioService = audioService;
        _trayHost = trayHost;
        _notificationService = notificationService;
        _dependencyService = dependencyService ?? new LemonadeDependencyService(chatClient);
    }

    private void UpdateState(TrayIconState state)
    {
        lock (_stateLock)
        {
            if (CurrentState == state) return;
            CurrentState = state;
        }

        try { _trayHost.SetState(state); }
        catch (Exception ex) { AppLog.Warn("Tray icon update failed", ex); }

        var handlers = StateChanged;
        if (handlers == null) return;

        // A UI handler must never be able to break (or silently reroute) the speech pipeline.
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<TrayIconState>)handler)(state); }
            catch (Exception ex) { AppLog.Warn($"State handler failed for {state}", ex); }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            CancelCurrentPipeline();
            _lastSpokenText = null;
        }

        _audioService.Stop();
        UpdateState(TrayIconState.Idle);
    }

    public void ToggleSpeakMode()
    {
        var settings = _settingsStore.Current;
        settings.SpeakMode = settings.SpeakMode == SpeakMode.Summary
            ? SpeakMode.Verbatim
            : SpeakMode.Summary;
        _settingsStore.Save(settings);

        var label = settings.SpeakMode == SpeakMode.Summary ? "Mode: Summary" : "Mode: Verbatim";
        AppLog.Info($"speak mode -> {settings.SpeakMode}");
        RaiseNotice(label);

        try { SpeakModeChanged?.Invoke(settings.SpeakMode); }
        catch (Exception ex) { AppLog.Warn("SpeakModeChanged handler failed", ex); }
    }

    public async Task SpeakSelectionAsync()
    {
        var settings = _settingsStore.Current;
        bool wasBusy = IsBusy;
        var clock = Stopwatch.StartNew();

        if (!wasBusy)
            UpdateState(TrayIconState.Capturing);

        // 1. Capture highlighted text from selection
        string? capturedText;
        try
        {
            capturedText = await _clipboardService.CaptureSelectedTextAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Selection capture failed", ex);
            capturedText = null;
        }
        AppLog.Info($"hotkey: captured {capturedText?.Length ?? 0} chars in {clock.ElapsedMilliseconds} ms (busy before: {wasBusy}, mode: {settings.SpeakMode})");

        // 2. If nothing is selected
        if (string.IsNullOrWhiteSpace(capturedText))
        {
            if (wasBusy)
            {
                Stop();
                if (settings.PlaySoundOnStop)
                {
                    _audioService.PlayStopChime();
                }
            }
            else
            {
                if (CurrentState == TrayIconState.Capturing)
                    UpdateState(TrayIconState.Idle);
                RaiseNotice("No text selected");
            }
            return;
        }

        // 3. If currently busy and the selection hasn't changed, stop playback
        if (wasBusy && string.Equals(capturedText, _lastSpokenText, StringComparison.Ordinal))
        {
            Stop();
            if (settings.PlaySoundOnStop)
            {
                _audioService.PlayStopChime();
            }
            return;
        }

        // 4. New selection or not busy: optionally summarize, then speak
        await SpeakOrSummarizeCapturedAsync(capturedText);
    }

    /// <summary>Speaks (or summarizes then speaks) already-captured text. Used by the hotkey path and tests.</summary>
    public async Task SpeakOrSummarizeCapturedAsync(string capturedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturedText);

        var settings = _settingsStore.Current;
        _lastSpokenText = capturedText;
        var textToSpeak = capturedText;

        if (settings.SpeakMode == SpeakMode.Summary)
        {
            CancellationTokenSource summarizeCts;
            lock (_lock)
            {
                CancelCurrentPipeline();
                _currentCts = new CancellationTokenSource();
                summarizeCts = _currentCts;
            }

            UpdateState(TrayIconState.Summarizing);
            var summarizeClock = Stopwatch.StartNew();
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        RaiseProgress(msg);
                });

                var modelId = await _dependencyService.EnsureChatModelAsync(
                    settings.ChatEndpoint,
                    settings.SummaryModel,
                    progress,
                    summarizeCts.Token);

                if (string.IsNullOrWhiteSpace(settings.SummaryModel) ||
                    !string.Equals(settings.SummaryModel, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = settings.Clone();
                    updated.SummaryModel = modelId;
                    _settingsStore.Save(updated);
                    settings = updated;
                }

                RaiseProgress("Summarizing…");
                textToSpeak = await _chatClient.SummarizeAsync(
                    settings.ChatEndpoint,
                    modelId,
                    capturedText,
                    summarizeCts.Token);

                AppLog.Info($"summarize: {capturedText.Length} chars -> {textToSpeak.Length} chars in {summarizeClock.ElapsedMilliseconds} ms");

                if (string.IsNullOrWhiteSpace(textToSpeak))
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_currentCts, summarizeCts))
                        {
                            _currentCts = null;
                            _lastSpokenText = null;
                            UpdateState(TrayIconState.Idle);
                        }
                    }
                    RaiseNotice("Summary was empty");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_currentCts, summarizeCts))
                    {
                        _currentCts = null;
                        _lastSpokenText = null;
                        UpdateState(TrayIconState.Idle);
                    }
                }
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Summarization failed", ex);
                lock (_lock)
                {
                    if (ReferenceEquals(_currentCts, summarizeCts))
                    {
                        _currentCts = null;
                        _lastSpokenText = null;
                        UpdateState(TrayIconState.Idle);
                    }
                }

                // Raise notice after Idle so the status bubble will show it (notices are ignored while busy).
                var shortNotice = ex.Message.Contains("Lemonade", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("chat LLM", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("cannot summarize", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("TTS models", StringComparison.OrdinalIgnoreCase)
                    ? "Need Lemonade for Summary"
                    : "Summary failed";
                RaiseNotice(shortNotice);
                // Balloons truncate; keep title short and put the actionable detail in the body start.
                var balloon = ex.Message.Length <= 220 ? ex.Message : ex.Message[..220] + "…";
                _notificationService.ShowError("Dynamite TTS Summary", balloon);
                return;
            }
        }

        await StartPipelineAsync(textToSpeak: textToSpeak, overrideSettings: null);
    }

    public Task SpeakTextAsync(string text, AppSettings? overrideSettings = null)
    {
        _lastSpokenText = text;
        return StartPipelineAsync(textToSpeak: text, overrideSettings: overrideSettings);
    }

    public Task SpeakTextAsync(string text, string? overrideVoice)
    {
        var settings = _settingsStore.Current;
        if (!string.IsNullOrEmpty(overrideVoice))
        {
            settings.Voice = overrideVoice;
        }
        _lastSpokenText = text;
        return StartPipelineAsync(textToSpeak: text, overrideSettings: settings);
    }

    private void RaiseProgress(string message)
    {
        try { ProgressMessage?.Invoke(message); }
        catch (Exception ex) { AppLog.Warn("ProgressMessage handler failed", ex); }
    }

    private void RaiseNotice(string message)
    {
        try { Notice?.Invoke(message); }
        catch (Exception ex) { AppLog.Warn("Notice handler failed", ex); }
    }

    private sealed class PipelineRun
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public PipelineRun(int textLength) => TextLength = textLength;

        public int TextLength { get; }
        public string Engine = "?";
        public int Segments;
        public double AudioSeconds;
        public double FirstSegmentMs = -1;
        public double FirstAudioMs = -1;
        public double SynthesisDoneMs = -1;
        public double PlaybackDoneMs = -1;
        public double UnderrunSeconds;
        public bool UsedFallback;
        public string? FallbackReason;
        public bool Cancelled;

        public double Now => _clock.Elapsed.TotalMilliseconds;

        public PipelineMetrics ToMetrics() => new(
            Engine, TextLength, Segments, AudioSeconds, FirstSegmentMs, FirstAudioMs,
            SynthesisDoneMs, PlaybackDoneMs, UnderrunSeconds, UsedFallback, FallbackReason, Cancelled);
    }

    private async Task StartPipelineAsync(string textToSpeak, AppSettings? overrideSettings = null)
    {
        CancellationTokenSource cts;

        lock (_lock)
        {
            CancelCurrentPipeline();
            _currentCts = new CancellationTokenSource();
            cts = _currentCts;
        }

        _audioService.Stop();
        UpdateState(TrayIconState.Synthesizing);

        var run = new PipelineRun(textToSpeak.Length);
        try
        {
            var settings = overrideSettings ?? _settingsStore.Current;
            var voice = settings.Voice;
            var chunks = SentenceChunker.SplitIntoChunks(textToSpeak);

            if (chunks.Count == 0 || cts.IsCancellationRequested)
            {
                UpdateState(TrayIconState.Idle);
                return;
            }

            run.Engine = settings.EngineMode;
            AppLog.Info($"speak: {textToSpeak.Length} chars in {chunks.Count} chunk(s), engine {settings.EngineMode}, voice {voice}, speed {settings.Speed:F2}");

            await ExecuteChunkPipelineAsync(chunks, settings, voice, run, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation / stop / preemption
            run.Cancelled = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Speech pipeline failed", ex);
            _notificationService.ShowError("Dynamite TTS Error", ex.Message);
        }
        finally
        {
            var metrics = run.ToMetrics();
            LastMetrics = metrics;
            AppLog.Info("speak done: " + metrics);
            try { PipelineCompleted?.Invoke(metrics); } catch { }

            lock (_lock)
            {
                if (ReferenceEquals(_currentCts, cts))
                {
                    _currentCts = null;
                    _lastSpokenText = null;
                    UpdateState(TrayIconState.Idle);
                }
            }
        }
    }

    private async Task ExecuteChunkPipelineAsync(
        IReadOnlyList<string> chunks,
        AppSettings settings,
        string voice,
        PipelineRun run,
        CancellationToken ct)
    {
        if (string.Equals(settings.EngineMode, "DirectML", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await ExecuteLocalStreamingPipelineAsync(chunks, settings, voice, run, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Previously swallowed silently, which turned a streaming failure into
                // "render the whole chunk, then play it". Record why.
                run.UsedFallback = true;
                run.FallbackReason = $"{ex.GetType().Name}: {ex.Message}";
                AppLog.Warn("Local streaming pipeline failed; falling back to the non-streaming path", ex);
                _audioService.Stop();
                UpdateState(TrayIconState.Synthesizing);
            }
        }

        run.Segments = chunks.Count;

        if (chunks.Count == 1)
        {
            UpdateState(TrayIconState.Synthesizing);
            var audioData = await GenerateChunkAudioAsync(settings, chunks[0], voice, ct);
            run.SynthesisDoneMs = run.Now;

            run.FirstAudioMs = run.Now;
            UpdateState(TrayIconState.Speaking);
            await _audioService.PlayAsync(audioData, settings.AudioDeviceId, ct);
            run.PlaybackDoneMs = run.Now;
            return;
        }

        UpdateState(TrayIconState.Synthesizing);
        var nextFetchTask = GenerateChunkAudioAsync(settings, chunks[0], voice, ct);

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var currentAudio = await nextFetchTask;

            if (i + 1 < chunks.Count)
            {
                var nextIndex = i + 1;
                nextFetchTask = GenerateChunkAudioAsync(settings, chunks[nextIndex], voice, ct);
            }
            else
            {
                run.SynthesisDoneMs = run.Now;
            }

            if (run.FirstAudioMs < 0) run.FirstAudioMs = run.Now;
            UpdateState(TrayIconState.Speaking);
            await _audioService.PlayAsync(currentAudio, settings.AudioDeviceId, ct);
        }

        run.PlaybackDoneMs = run.Now;
    }

    /// <summary>The in-process Kokoro engine (exposed so the UI can show which device is active).</summary>
    public LocalTtsService LocalEngine => _localTtsService;

    /// <summary>
    /// Local engine path: one gapless playback stream for the whole selection. The engine renders
    /// short segments and writes them into the stream as they finish, so playback of the first
    /// sentence starts while the rest is still being synthesized; the stream applies back-pressure
    /// so synthesis stays only a few seconds ahead of playback.
    /// </summary>
    private async Task ExecuteLocalStreamingPipelineAsync(
        IReadOnlyList<string> chunks,
        AppSettings settings,
        string voice,
        PipelineRun run,
        CancellationToken ct)
    {
        UpdateState(TrayIconState.Synthesizing);

        using var stream = _audioService.BeginStream(LocalTtsService.SampleRate, settings.AudioDeviceId, ct);

        // "Speaking" means the listener is hearing audio, not merely that a segment was rendered.
        stream.PlaybackStarted += () =>
        {
            if (ct.IsCancellationRequested || run.PlaybackDoneMs >= 0) return;
            run.FirstAudioMs = run.Now;
            UpdateState(TrayIconState.Speaking);
        };

        await _localTtsService.GenerateSpeechStreamingAsync(
            chunks,
            voice,
            settings.Speed,
            settings.DirectMlDeviceId,
            settings.DirectMlModelPrecision,
            onSegmentAsync: async samples =>
            {
                if (run.Segments == 0) run.FirstSegmentMs = run.Now;
                run.Segments++;
                run.AudioSeconds += samples.Length / (double)LocalTtsService.SampleRate;
                await stream.WriteAsync(samples, ct);
            },
            ct);

        run.SynthesisDoneMs = run.Now;
        run.Engine = $"local/{_localTtsService.State}";
        AppLog.Info($"speak: synthesis finished at {run.SynthesisDoneMs:F0} ms; {stream.PlayedSeconds:F1}s of {run.AudioSeconds:F1}s already played");

        await stream.CompleteAsync();
        run.PlaybackDoneMs = run.Now;
        run.UnderrunSeconds = stream.UnderrunSamples / (double)LocalTtsService.SampleRate;
    }

    private async Task<byte[]> GenerateChunkAudioAsync(
        AppSettings settings,
        string chunk,
        string voice,
        CancellationToken ct)
    {
        if (string.Equals(settings.EngineMode, "DirectML", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await _localTtsService.GenerateSpeechWavAsync(
                    chunk,
                    voice,
                    settings.Speed,
                    settings.DirectMlDeviceId,
                    settings.DirectMlModelPrecision,
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AppLog.Warn("Local WAV synthesis failed; falling back to Lemonade Server", ex);
                // Fallback to Lemonade Server if local engine encountered an issue
                return await _ttsClient.GenerateSpeechAsync(
                    settings.SpeechEndpoint,
                    chunk,
                    settings.Model,
                    voice,
                    settings.Speed,
                    settings.ResponseFormat,
                    ct);
            }
        }
        else
        {
            return await _ttsClient.GenerateSpeechAsync(
                settings.SpeechEndpoint,
                chunk,
                settings.Model,
                voice,
                settings.Speed,
                settings.ResponseFormat,
                ct);
        }
    }

    private void CancelCurrentPipeline()
    {
        if (_currentCts != null)
        {
            try
            {
                _currentCts.Cancel();
                _currentCts.Dispose();
            }
            catch { }
            _currentCts = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _ttsClient.Dispose();
        _chatClient.Dispose();
        _localTtsService.Dispose();
        _audioService.Dispose();
    }
}
