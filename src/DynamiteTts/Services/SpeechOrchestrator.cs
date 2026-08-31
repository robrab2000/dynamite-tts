using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Native;

namespace DynamiteTts.Services;

public class SpeechOrchestrator : IDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ClipboardSelectionService _clipboardService;
    private readonly LemonadeTtsClient _ttsClient;
    private readonly AudioPlaybackService _audioService;
    private readonly TrayIconHost _trayHost;
    private readonly TrayNotificationService _notificationService;

    private readonly object _lock = new();
    private CancellationTokenSource? _currentCts;
    private string? _lastSpokenText;
    private bool _isDisposed;

    public event Action<TrayIconState>? StateChanged;

    public TrayIconState CurrentState { get; private set; } = TrayIconState.Idle;

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
        AudioPlaybackService audioService,
        TrayIconHost trayHost,
        TrayNotificationService notificationService)
    {
        _settingsStore = settingsStore;
        _clipboardService = clipboardService;
        _ttsClient = ttsClient;
        _audioService = audioService;
        _trayHost = trayHost;
        _notificationService = notificationService;
    }

    private void UpdateState(TrayIconState state)
    {
        CurrentState = state;
        _trayHost.SetState(state);
        StateChanged?.Invoke(state);
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

    public async Task SpeakSelectionAsync()
    {
        var settings = _settingsStore.Current;
        bool wasBusy = IsBusy;

        // 1. Capture highlighted text from selection
        var capturedText = await _clipboardService.CaptureSelectedTextAsync();

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

        // 4. New selection or not busy: speak the new selection
        _lastSpokenText = capturedText;
        await StartPipelineAsync(textToSpeak: capturedText, overrideSettings: null);
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

            await ExecuteChunkPipelineAsync(chunks, settings, voice, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation / stop / preemption
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Dynamite TTS Error", ex.Message);
        }
        finally
        {
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
        CancellationToken ct)
    {
        if (chunks.Count == 1)
        {
            UpdateState(TrayIconState.Synthesizing);
            var audioData = await _ttsClient.GenerateSpeechAsync(
                settings.SpeechEndpoint,
                chunks[0],
                settings.Model,
                voice,
                settings.Speed,
                settings.ResponseFormat,
                ct);

            UpdateState(TrayIconState.Speaking);
            await _audioService.PlayAsync(audioData, settings.AudioDeviceId, ct);
            return;
        }

        // Multiple chunks with lookahead pre-fetching
        UpdateState(TrayIconState.Synthesizing);
        var nextFetchTask = _ttsClient.GenerateSpeechAsync(
            settings.SpeechEndpoint,
            chunks[0],
            settings.Model,
            voice,
            settings.Speed,
            settings.ResponseFormat,
            ct);

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var currentAudio = await nextFetchTask;

            // Start pre-fetching the next chunk ahead of time
            if (i + 1 < chunks.Count)
            {
                var nextIndex = i + 1;
                nextFetchTask = _ttsClient.GenerateSpeechAsync(
                    settings.SpeechEndpoint,
                    chunks[nextIndex],
                    settings.Model,
                    voice,
                    settings.Speed,
                    settings.ResponseFormat,
                    ct);
            }

            UpdateState(TrayIconState.Speaking);
            await _audioService.PlayAsync(currentAudio, settings.AudioDeviceId, ct);
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
        _audioService.Dispose();
    }
}
