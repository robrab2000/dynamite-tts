using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private LocalTtsService? _localTtsService;
    private DirectMlDeviceService? _directMlDeviceService;
    private AudioDeviceService? _audioDeviceService;
    private AudioPlaybackService? _audioPlaybackService;
    private ClipboardSelectionService? _clipboardService;
    private StartupRegistrationService? _startupService;
    private LemonadeDependencyService? _dependencyService;
    private SpeechOrchestrator? _orchestrator;
    private HotkeyService? _hotkeyService;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        _settingsStore = new AppSettingsStore();
        _trayHost = new TrayIconHost();
        _notificationService = new TrayNotificationService(_trayHost);
        _ttsClient = new LemonadeTtsClient();
        _localTtsService = new LocalTtsService();
        _directMlDeviceService = new DirectMlDeviceService();
        _audioDeviceService = new AudioDeviceService();
        _audioPlaybackService = new AudioPlaybackService();
        _clipboardService = new ClipboardSelectionService();
        _startupService = new StartupRegistrationService();
        _dependencyService = new LemonadeDependencyService();

        _orchestrator = new SpeechOrchestrator(
            _settingsStore,
            _clipboardService,
            _ttsClient,
            _localTtsService,
            _audioPlaybackService,
            _trayHost,
            _notificationService);

        _hotkeyService = new HotkeyService(_trayHost.Handle);
        _hotkeyService.HotkeyConflictOccurred += msg =>
        {
            _notificationService.ShowWarning("Shortcut Conflict", msg);
        };

        // 3. Register Global Hotkey
        var settings = _settingsStore.Current;
        if (!_hotkeyService.TryRegister(settings.Hotkey, out var hotkeyError))
        {
            _notificationService.ShowWarning("Shortcut Registration", hotkeyError ?? "Could not register default shortcut.");
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
            _hotkeyService,
            _directMlDeviceService,
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
                    await _localTtsService.InitializeAsync(
                        useDirectMl: settings.UseDirectMlAcceleration,
                        deviceId: settings.DirectMlDeviceId,
                        precision: settings.DirectMlModelPrecision);
                }
                else
                {
                    await Task.Delay(500);
                    await _ttsClient.PrewarmAsync(settings.SpeechEndpoint, settings.Model, settings.Voice);
                }
            }
            catch { }
        });
    }

    private async void OnHotkeyPressed()
    {
        if (_orchestrator != null)
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
