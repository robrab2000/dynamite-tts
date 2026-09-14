using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DynamiteTts.Models;
using DynamiteTts.Native;
using DynamiteTts.Services;
using DynamiteTts.UI;

namespace DynamiteTts;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\DynamiteTts_SessionMutex";
    private static Mutex? _singleInstanceMutex;

    private AppSettingsStore? _settingsStore;
    private TrayIconHost? _trayHost;
    private TrayNotificationService? _notificationService;
    private LemonadeTtsClient? _ttsClient;
    private LemonadeChatClient? _chatClient;
    private LocalTtsService? _localTtsService;
    private CudaDeviceService? _deviceService;
    private LocalEngineState _lastNotifiedEngineState = LocalEngineState.NotInitialized;
    private AudioDeviceService? _audioDeviceService;
    private AudioPlaybackService? _audioPlaybackService;
    private ClipboardSelectionService? _clipboardService;
    private StartupRegistrationService? _startupService;
    private LemonadeDependencyService? _dependencyService;
    private SpeechOrchestrator? _orchestrator;
    private HotkeyService? _hotkeyService;
    private SettingsWindow? _settingsWindow;
    private StatusBubbleWindow? _statusBubble;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launched at login the working directory is System32. KokoroSharp (voices) and the model
        // lookup resolve files relative to it, so anchor it to the executable's folder.
        try { System.IO.Directory.SetCurrentDirectory(AppContext.BaseDirectory); } catch { }

        // 1. Single Instance Check
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            // Another instance is already running; signal it to show settings
            var msg = NativeMethods.RegisterWindowMessage("DynamiteTts_ShowSettings");
            if (msg != 0)
            {
                NativeMethods.PostMessage((IntPtr)0xffff, msg, IntPtr.Zero, IntPtr.Zero); // HWND_BROADCAST
            }

            Shutdown();
            return;
        }

        try
        {
            InitializeApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize Dynamite TTS:\n{ex.Message}", "Dynamite TTS Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void InitializeApplication()
    {
        // 2. Services Initialization
        AppLog.Info($"Dynamite TTS starting (pid {Environment.ProcessId})");
        _settingsStore = new AppSettingsStore();
        _trayHost = new TrayIconHost();
        _notificationService = new TrayNotificationService(_trayHost);
        _ttsClient = new LemonadeTtsClient();
        _chatClient = new LemonadeChatClient();
        _localTtsService = new LocalTtsService();
        _deviceService = new CudaDeviceService();
        _localTtsService.StatusChanged += OnLocalEngineStatusChanged;
        _audioDeviceService = new AudioDeviceService();
        _audioPlaybackService = new AudioPlaybackService();
        _clipboardService = new ClipboardSelectionService();
        _startupService = new StartupRegistrationService();
        _dependencyService = new LemonadeDependencyService();

        _orchestrator = new SpeechOrchestrator(
            _settingsStore,
            _clipboardService,
            _ttsClient,
            _chatClient,
            _localTtsService,
            _audioPlaybackService,
            _trayHost,
            _notificationService);

        // Handy-style status pill at the top of the screen while capturing, synthesizing and speaking.
        _statusBubble = new StatusBubbleWindow(() => _audioPlaybackService?.OutputLevel ?? 0f);
        _statusBubble.StopRequested += () => _orchestrator?.Stop();
        _orchestrator.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            if (_settingsStore?.Current.ShowStatusBubble != false) _statusBubble?.ShowState(state);
            else if (state == TrayIconState.Idle) _statusBubble?.ShowState(state);
        });
        _orchestrator.Notice += message => Dispatcher.BeginInvoke(() =>
        {
            if (_settingsStore?.Current.ShowStatusBubble != false) _statusBubble?.ShowNotice(message);
        });

        _hotkeyService = new HotkeyService(_trayHost.Handle);
        _hotkeyService.HotkeyConflictOccurred += msg =>
        {
            _notificationService.ShowWarning("Shortcut Conflict", msg);
        };

        // 3. Register Global Hotkeys (speak + mode toggle)
        var settings = _settingsStore.Current;
        if (!_hotkeyService.TryRegisterPair(settings.Hotkey, settings.ModeToggleHotkey, out var hotkeyError))
        {
            _notificationService.ShowWarning("Shortcut Registration", hotkeyError ?? "Could not register shortcuts.");
        }

        // 4. Sync Startup Registration if needed
        if (settings.RunAtStartup && !_startupService.IsStartupEnabled())
        {
            _startupService.SetStartupEnabled(true);
        }

        // 5. Initialize Settings Window
        _settingsWindow = new SettingsWindow(
            _settingsStore,
            _ttsClient,
            _chatClient,
            _hotkeyService,
            _deviceService,
            _audioDeviceService,
            _startupService,
            _dependencyService,
            _orchestrator,
            _notificationService);

        // 6. Wire Tray Events
        _trayHost.HotkeyPressed += OnHotkeyPressed;
        _trayHost.StopRequested += OnStopRequested;
        _trayHost.SettingsRequested += OnSettingsRequested;
        _trayHost.RefreshModelsRequested += OnRefreshModelsRequested;
        _trayHost.ExitRequested += OnExitRequested;

        // 7. Background Pre-warm Audio Stream & TTS Engine
        _ = Task.Run(async () =>
        {
            try
            {
                _audioPlaybackService.WarmUp(settings.AudioDeviceId);
                if (string.Equals(settings.EngineMode, "DirectML", StringComparison.OrdinalIgnoreCase))
                {
                    _localTtsService.GpuIdleTimeout = TimeSpan.FromMinutes(Math.Max(1, settings.GpuIdleUnloadMinutes));
                    await _localTtsService.InitializeAsync(
                        useGpu: settings.UseDirectMlAcceleration,
                        deviceId: settings.DirectMlDeviceId,
                        precision: settings.DirectMlModelPrecision);
                }
                else
                {
                    await Task.Delay(500);
                    await _ttsClient.PrewarmAsync(settings.SpeechEndpoint, settings.Model, settings.Voice);
                }
            }
            catch (Exception ex)
            {
                // Previously swallowed silently, leaving the engine stuck on "Loading" with no trace.
                AppLog.Error("Startup pre-warm failed", ex);
            }
        });
    }

    /// <summary>Tray balloons for the one-off CUDA runtime download so a silent 1 GB download is never a surprise.</summary>
    private void OnLocalEngineStatusChanged(string description)
    {
        var engine = _localTtsService;
        if (engine == null || _notificationService == null) return;

        var state = engine.State;
        if (state == _lastNotifiedEngineState) return;
        var previous = _lastNotifiedEngineState;
        _lastNotifiedEngineState = state;

        string? title = null, message = null;
        var warning = false;
        switch (state)
        {
            case LocalEngineState.GpuRuntimeDownloading:
                title = "Downloading GPU acceleration";
                message = $"Fetching the NVIDIA CUDA runtime ({CudaRuntimeService.TotalDownloadBytes / 1_000_000} MB) for {engine.GpuName}. Speech keeps working on the CPU meanwhile.";
                break;
            case LocalEngineState.GpuIdle when previous == LocalEngineState.GpuRuntimeDownloading:
                title = "GPU acceleration ready";
                message = $"{engine.GpuName} will be used for speech from now on.";
                break;
            case LocalEngineState.GpuRuntimeUnavailable:
            case LocalEngineState.GpuFailed:
                title = "GPU acceleration unavailable";
                message = description;
                warning = true;
                break;
        }

        if (title == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (warning) _notificationService.ShowWarning(title, message!);
            else _notificationService.ShowInfo(title, message!);
        });
    }

    private async void OnHotkeyPressed(int hotkeyId)
    {
        if (_orchestrator == null) return;

        if (hotkeyId == HotkeyService.ModeToggleHotkeyId)
        {
            _orchestrator.ToggleSpeakMode();
            return;
        }

        if (hotkeyId == HotkeyService.SpeakHotkeyId)
        {
            await _orchestrator.SpeakSelectionAsync();
        }
    }

    private void OnStopRequested()
    {
        _orchestrator?.Stop();
    }

    private void OnSettingsRequested()
    {
        _settingsWindow?.ShowSettings();
    }

    private async void OnRefreshModelsRequested()
    {
        if (_ttsClient == null || _settingsStore == null || _notificationService == null) return;

        try
        {
            var endpoint = _settingsStore.Current.SpeechEndpoint;
            var models = await _ttsClient.GetModelsAsync(endpoint);
            _notificationService.ShowInfo("Models Refreshed", $"Discovered {models.Count} models from Lemonade Server.");
        }
        catch (Exception ex)
        {
            _notificationService.ShowWarning("Refresh Failed", ex.Message);
        }
    }

    private void OnExitRequested()
    {
        ExitApplication();
    }

    private void ExitApplication()
    {
        try
        {
            _orchestrator?.Stop();
            _settingsWindow?.ShutdownWindow();
            _statusBubble?.Close();
            _hotkeyService?.Dispose();
            _orchestrator?.Dispose();
            _localTtsService?.Dispose();
            _trayHost?.Dispose();
        }
        catch { }

        try
        {
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch { }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ExitApplication();
        base.OnExit(e);
    }
}
