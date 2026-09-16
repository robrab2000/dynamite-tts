using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamiteTts.Models;
using DynamiteTts.Native;
using DynamiteTts.Services;

namespace DynamiteTts.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _settingsStore;
    private readonly LemonadeTtsClient _ttsClient;
    private readonly LemonadeChatClient _chatClient;
    private readonly HotkeyService _hotkeyService;
    private readonly CudaDeviceService _deviceService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly StartupRegistrationService _startupService;
    private readonly LemonadeDependencyService _dependencyService;
    private readonly SpeechOrchestrator _orchestrator;
    private readonly TrayNotificationService _notificationService;

    private bool _isExplicitClose;

    public SettingsWindow(
        AppSettingsStore settingsStore,
        LemonadeTtsClient ttsClient,
        LemonadeChatClient chatClient,
        HotkeyService hotkeyService,
        CudaDeviceService deviceService,
        AudioDeviceService audioDeviceService,
        StartupRegistrationService startupService,
        LemonadeDependencyService dependencyService,
        SpeechOrchestrator orchestrator,
        TrayNotificationService notificationService)
    {
        InitializeComponent();

        _settingsStore = settingsStore;
        _ttsClient = ttsClient;
        _chatClient = chatClient;
        _hotkeyService = hotkeyService;
        _deviceService = deviceService;
        _audioDeviceService = audioDeviceService;
        _startupService = startupService;
        _dependencyService = dependencyService;
        _orchestrator = orchestrator;
        _notificationService = notificationService;

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
        _orchestrator.SpeakModeChanged += OnSpeakModeChanged;
        _orchestrator.LocalEngine.StatusChanged += OnLocalEngineStatusChanged;

        Loaded += OnLoaded;
    }

    private void OnSpeakModeChanged(SpeakMode mode)
    {
        Dispatcher.BeginInvoke(() => SelectSpeakMode(mode));
    }

    private void OnLocalEngineStatusChanged(string _)
    {
        Dispatcher.BeginInvoke(UpdateLocalEngineStatus);
    }

    /// <summary>Shows what the in-process engine is really doing (CPU, GPU idle/loading/active, runtime download).</summary>
    private void UpdateLocalEngineStatus()
    {
        if (DirectMlStatusTitle == null || DirectMlStatusDetails == null || DirectMlStatusDot == null)
            return;

        var engine = _orchestrator.LocalEngine;
        var selected = DirectMlDeviceComboBox?.SelectedItem as AccelerationDeviceInfo;
        var selectionNote = selected != null && selected.IsSelectable && selected.DeviceId != engine.CurrentDeviceId
            ? $" The selected accelerator ({selected.Description}) is applied after you save."
            : string.Empty;

        string title, details, brush;
        switch (engine.State)
        {
            case LocalEngineState.NotInitialized:
            case LocalEngineState.Loading:
                title = "Local Kokoro engine is loading…";
                details = "The model is being loaded and warmed up in the background.";
                brush = "WarningBrush";
                break;
            case LocalEngineState.CpuOnly:
                title = engine.ActiveDeviceDescription;
                details = "Speech streams sentence by sentence; playback starts after about 0.7 s.";
                brush = "AccentBrush";
                break;
            case LocalEngineState.GpuRuntimeDownloading:
                title = "Preparing GPU acceleration…";
                details = engine.ActiveDeviceDescription + " The download happens once and is kept in your local app data.";
                brush = "WarningBrush";
                break;
            case LocalEngineState.GpuRuntimeUnavailable:
            case LocalEngineState.GpuFailed:
                title = "Running on CPU — GPU acceleration unavailable";
                details = engine.ActiveDeviceDescription + " Press Refresh or restart the app to try again.";
                brush = "WarningBrush";
                break;
            case LocalEngineState.GpuIdle:
                title = $"GPU acceleration ready ({engine.GpuName})";
                details = engine.ActiveDeviceDescription + " While the GPU session loads (a few seconds), the CPU renders the first sentences so nothing waits.";
                brush = "SuccessBrush";
                break;
            case LocalEngineState.GpuLoading:
                title = $"GPU session loading ({engine.GpuName})";
                details = engine.ActiveDeviceDescription;
                brush = "WarningBrush";
                break;
            default:
                title = $"GPU acceleration active ({engine.ActiveDeviceDescription})";
                details = $"Segments render on the GPU; the session is unloaded after {Math.Max(1, _settingsStore.Current.GpuIdleUnloadMinutes)} idle minutes so the GPU can sleep.";
                brush = "SuccessBrush";
                break;
        }

        DirectMlStatusTitle.Text = title;
        DirectMlStatusTitle.Foreground = (Brush)FindResource(brush);
        DirectMlStatusDot.Fill = (Brush)FindResource(brush);
        DirectMlStatusDetails.Text = details + selectionNote;
    }

    private void OnOrchestratorStateChanged(TrayIconState state)
    {
        // BeginInvoke: state changes can arrive from audio/worker threads and must never block them.
        Dispatcher.BeginInvoke(() =>
        {
            switch (state)
            {
                case TrayIconState.Capturing:
                    SpeechActivityPanel.Visibility = Visibility.Visible;
                    SpeechActivityDot.Fill = (Brush)FindResource("TextSecondaryBrush");
                    SpeechActivityTextBlock.Text = "Reading selection...";
                    SpeechActivityTextBlock.Foreground = (Brush)FindResource("TextSecondaryBrush");
                    break;

                case TrayIconState.Summarizing:
                    SpeechActivityPanel.Visibility = Visibility.Visible;
                    SpeechActivityDot.Fill = (Brush)FindResource("WarningBrush");
                    SpeechActivityTextBlock.Text = "Summarizing...";
                    SpeechActivityTextBlock.Foreground = (Brush)FindResource("WarningBrush");
                    TestSpeechButton.Content = "⏹ Stop Speech";
                    PreviewVoiceButton.Content = "⏹ Stop";
                    break;

                case TrayIconState.Synthesizing:
                    SpeechActivityPanel.Visibility = Visibility.Visible;
                    SpeechActivityDot.Fill = (Brush)FindResource("WarningBrush");
                    SpeechActivityTextBlock.Text = "Synthesizing audio...";
                    SpeechActivityTextBlock.Foreground = (Brush)FindResource("WarningBrush");
                    TestSpeechButton.Content = "⏹ Stop Speech";
                    PreviewVoiceButton.Content = "⏹ Stop";
                    break;

                case TrayIconState.Speaking:
                    SpeechActivityPanel.Visibility = Visibility.Visible;
                    SpeechActivityDot.Fill = (Brush)FindResource("SuccessBrush");
                    SpeechActivityTextBlock.Text = "Speaking audio...";
                    SpeechActivityTextBlock.Foreground = (Brush)FindResource("SuccessBrush");
                    TestSpeechButton.Content = "⏹ Stop Speech";
                    PreviewVoiceButton.Content = "⏹ Stop";
                    break;

                case TrayIconState.Idle:
                default:
                    SpeechActivityPanel.Visibility = Visibility.Collapsed;
                    TestSpeechButton.Content = "Test Speech";
                    PreviewVoiceButton.Content = "Preview Voice";
                    break;
            }
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateVoices();
        RefreshAudioDevices();
        RefreshDirectMlDevices();
        PopulateFromSettings(_settingsStore.Current);
        UpdateLocalEngineStatus();
        await RefreshModelsAsync();
        await RefreshSummaryModelsAsync();
        await CheckConnectionAsync();
    }

    public void ShowSettings()
    {
        PopulateVoices();
        RefreshAudioDevices();
        RefreshDirectMlDevices();
        PopulateFromSettings(_settingsStore.Current);
        UpdateLocalEngineStatus();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    public void ShutdownWindow()
    {
        _isExplicitClose = true;
        _orchestrator.StateChanged -= OnOrchestratorStateChanged;
        _orchestrator.LocalEngine.StatusChanged -= OnLocalEngineStatusChanged;
        Close();
    }

    private void PopulateVoices()
    {
        var currentVoice = VoiceComboBox.Text;
        if (string.IsNullOrEmpty(currentVoice))
        {
            currentVoice = _settingsStore.Current.Voice;
        }

        VoiceComboBox.Items.Clear();
        foreach (var preset in VoicePreset.Presets)
        {
            VoiceComboBox.Items.Add(new ComboBoxItem
            {
                Content = preset.DisplayName,
                Tag = preset.Id
            });
        }

        var matchingItem = VoiceComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), currentVoice, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(i.Content?.ToString(), currentVoice, StringComparison.OrdinalIgnoreCase));

        if (matchingItem != null)
        {
            VoiceComboBox.SelectedItem = matchingItem;
        }
        else
        {
            VoiceComboBox.Text = currentVoice;
        }
    }

    private void RefreshDirectMlDevices()
    {
        var devices = _deviceService.GetAvailableDevices();
        DirectMlDeviceComboBox.ItemsSource = devices;
        DirectMlDeviceComboBox.DisplayMemberPath = "Name";
        DirectMlDeviceComboBox.SelectedValuePath = "DeviceId";

        SelectAccelerationDevice(_settingsStore.Current.DirectMlDeviceId);
    }

    /// <summary>Selects the saved accelerator, or Auto when the saved id no longer exists (e.g. an old DirectML adapter index).</summary>
    private void SelectAccelerationDevice(int deviceId)
    {
        if (DirectMlDeviceComboBox.ItemsSource is not System.Collections.Generic.IEnumerable<AccelerationDeviceInfo> devices)
            return;

        var list = devices.ToList();
        var match = list.FirstOrDefault(d => d.DeviceId == deviceId && d.IsSelectable)
                    ?? list.FirstOrDefault(d => d.Kind == AccelerationDeviceKind.Auto)
                    ?? list.FirstOrDefault(d => d.IsSelectable);
        if (match != null)
            DirectMlDeviceComboBox.SelectedItem = match;
    }

    private void RefreshAudioDevices()
    {
        var devices = _audioDeviceService.GetOutputDevices();
        AudioDeviceComboBox.ItemsSource = devices;
        AudioDeviceComboBox.DisplayMemberPath = "Name";
        AudioDeviceComboBox.SelectedValuePath = "Id";

        var currentDeviceId = _settingsStore.Current.AudioDeviceId;
        var selected = devices.FirstOrDefault(d => d.Id == currentDeviceId) ?? devices.FirstOrDefault();
        if (selected != null)
        {
            AudioDeviceComboBox.SelectedItem = selected;
        }
    }

    private void PopulateFromSettings(AppSettings settings)
    {
        // Engine selection
        var isDirectMl = string.Equals(settings.EngineMode, "DirectML", StringComparison.OrdinalIgnoreCase);
        foreach (ComboBoxItem item in EngineComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), settings.EngineMode, StringComparison.OrdinalIgnoreCase))
            {
                EngineComboBox.SelectedItem = item;
                break;
            }
        }
        UpdateEnginePanelsVisibility(isDirectMl);

        // Precision selection
        foreach (ComboBoxItem item in DirectMlPrecisionComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), settings.DirectMlModelPrecision, StringComparison.OrdinalIgnoreCase))
            {
                DirectMlPrecisionComboBox.SelectedItem = item;
                break;
            }
        }

        // Accelerator selection
        SelectAccelerationDevice(settings.DirectMlDeviceId);

        EndpointTextBox.Text = settings.SpeechEndpoint;
        ModelComboBox.Text = settings.Model;
        ModelComboBox.SelectedItem = settings.Model;

        VoiceComboBox.Text = settings.Voice;
        VoiceComboBox.SelectedItem = settings.Voice;

        SpeedSlider.Value = settings.Speed;
        SpeedValueTextBlock.Text = $"{settings.Speed:F2}x";
        HotkeyCaptureControl.Hotkey = settings.Hotkey;
        ModeToggleHotkeyCaptureControl.Hotkey = settings.ModeToggleHotkey;
        SelectSpeakMode(settings.SpeakMode);
        ChatEndpointTextBox.Text = settings.ChatEndpoint;
        SummaryModelComboBox.Text = settings.SummaryModel;
        PlaySoundOnStopCheckBox.IsChecked = settings.PlaySoundOnStop;
        ShowStatusBubbleCheckBox.IsChecked = settings.ShowStatusBubble;
        LaunchAtStartupCheckBox.IsChecked = _startupService.IsStartupEnabled();

        if (AudioDeviceComboBox.ItemsSource is System.Collections.Generic.IEnumerable<AudioDeviceInfo> devices)
        {
            var match = devices.FirstOrDefault(d => d.Id == settings.AudioDeviceId) ?? devices.FirstOrDefault();
            if (match != null)
            {
                AudioDeviceComboBox.SelectedItem = match;
            }
        }
    }

    private void OnEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EngineComboBox?.SelectedItem is ComboBoxItem selectedItem)
        {
            var isDirectMl = string.Equals(selectedItem.Tag?.ToString(), "DirectML", StringComparison.OrdinalIgnoreCase);
            UpdateEnginePanelsVisibility(isDirectMl);
        }
    }

    private void OnDirectMlDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DirectMlDeviceComboBox?.SelectedItem is not AccelerationDeviceInfo deviceInfo)
            return;

        if (DirectMlStatusTitle == null || DirectMlStatusDetails == null || DirectMlStatusDot == null)
            return;

        if (deviceInfo.Kind == AccelerationDeviceKind.NpuUnavailable)
        {
            DirectMlStatusTitle.Text = "AMD Ryzen AI NPU unavailable for Kokoro TTS";
            DirectMlStatusTitle.Foreground = (Brush)FindResource("AccentBrush");
            DirectMlStatusDot.Fill = (Brush)FindResource("AccentBrush");
            DirectMlStatusDetails.Text = CudaDeviceService.NpuUnavailableReason;

            // Snap selection back to Auto
            var fallback = (DirectMlDeviceComboBox.ItemsSource as System.Collections.Generic.IEnumerable<AccelerationDeviceInfo>)?
                .FirstOrDefault(d => d.Kind == AccelerationDeviceKind.Auto);

            if (fallback != null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show(this,
                        CudaDeviceService.NpuUnavailableReason +
                        "\n\nSwitching you to: " + fallback.Name,
                        "NPU not available for TTS",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DirectMlDeviceComboBox.SelectedItem = fallback;
                });
            }

            return;
        }

        // Show what the engine is really running on rather than an optimistic label.
        UpdateLocalEngineStatus();
    }

    private void OnRefreshAdaptersClick(object sender, RoutedEventArgs e)
    {
        RefreshDirectMlDevices();
        // Also retry a failed runtime download / GPU session; the status line follows via StatusChanged.
        _ = _orchestrator.LocalEngine.RetryGpuAsync();
        UpdateLocalEngineStatus();
    }

    private void UpdateEnginePanelsVisibility(bool isDirectMl)
    {
        if (DirectMlInfoPanel != null)
        {
            DirectMlInfoPanel.Visibility = isDirectMl ? Visibility.Visible : Visibility.Collapsed;
        }
        if (LemonadeServerPanel != null)
        {
            LemonadeServerPanel.Visibility = isDirectMl ? Visibility.Collapsed : Visibility.Visible;
        }
        if (ModelSelectionContainer != null)
        {
            ModelSelectionContainer.Visibility = isDirectMl ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private AppSettings GetCurrentSettingsFromUi()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = _settingsStore.Current.SpeechEndpoint;

        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = _settingsStore.Current.Model;

        var selectedVoiceItem = VoiceComboBox.SelectedItem as ComboBoxItem;
        var voice = selectedVoiceItem?.Tag?.ToString() ?? VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = _settingsStore.Current.Voice;

        var selectedDevice = AudioDeviceComboBox.SelectedItem as AudioDeviceInfo;
        var audioDeviceId = selectedDevice?.Id ?? _settingsStore.Current.AudioDeviceId;

        var selectedEngine = (EngineComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "DirectML";

        var selectedDmlDevice = DirectMlDeviceComboBox.SelectedItem as AccelerationDeviceInfo;
        var dmlDeviceId = selectedDmlDevice?.DeviceId ?? _settingsStore.Current.DirectMlDeviceId;
        if (dmlDeviceId == -100 || selectedDmlDevice?.Kind == AccelerationDeviceKind.NpuUnavailable)
            dmlDeviceId = -1;

        var selectedPrecisionItem = DirectMlPrecisionComboBox.SelectedItem as ComboBoxItem;
        var precision = selectedPrecisionItem?.Tag?.ToString() ?? _settingsStore.Current.DirectMlModelPrecision;

        return new AppSettings
        {
            SpeechEndpoint = endpoint,
            Model = model,
            Voice = voice,
            Speed = Math.Round(SpeedSlider.Value, 2),
            ResponseFormat = "mp3",
            Hotkey = HotkeyCaptureControl.Hotkey ?? _settingsStore.Current.Hotkey,
            ModeToggleHotkey = ModeToggleHotkeyCaptureControl.Hotkey ?? _settingsStore.Current.ModeToggleHotkey,
            SpeakMode = GetSelectedSpeakMode(),
            ChatEndpoint = string.IsNullOrWhiteSpace(ChatEndpointTextBox.Text)
                ? _settingsStore.Current.ChatEndpoint
                : ChatEndpointTextBox.Text.Trim(),
            SummaryModel = SummaryModelComboBox.Text.Trim(),
            AudioDeviceId = audioDeviceId,
            PlaySoundOnStop = PlaySoundOnStopCheckBox.IsChecked == true,
            ShowStatusBubble = ShowStatusBubbleCheckBox.IsChecked == true,
            RunAtStartup = LaunchAtStartupCheckBox.IsChecked == true,
            EngineMode = selectedEngine,
            UseDirectMlAcceleration = true,
            DirectMlDeviceId = dmlDeviceId,
            DirectMlModelPrecision = precision,
            GpuIdleUnloadMinutes = _settingsStore.Current.GpuIdleUnloadMinutes
        };
    }

    private async Task RefreshModelsAsync()
    {
        RefreshModelsButton.IsEnabled = false;
        try
        {
            var endpoint = EndpointTextBox.Text.Trim();
            var models = await _ttsClient.GetModelsAsync(endpoint);

            var currentText = ModelComboBox.Text;
            if (string.IsNullOrEmpty(currentText))
            {
                currentText = _settingsStore.Current.Model;
            }

            ModelComboBox.Items.Clear();

            if (models.Count > 0)
            {
                foreach (var model in models)
                {
                    ModelComboBox.Items.Add(model.Id);
                }
            }
            else
            {
                ModelComboBox.Items.Add("kokoro-v1");
                ModelComboBox.Items.Add("OpenMOSS-TTS");
            }

            var chosenModel = !string.IsNullOrEmpty(currentText) ? currentText : "kokoro-v1";
            ModelComboBox.Text = chosenModel;
            ModelComboBox.SelectedItem = chosenModel;
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async Task CheckConnectionAsync()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        StatusTextBlock.Text = "Testing connection...";
        StatusDot.Fill = (Brush)FindResource("WarningBrush");

        var isConnected = await _ttsClient.TestConnectionAsync(endpoint);
        if (isConnected)
        {
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
            StatusTextBlock.Text = "Connected to Lemonade Server";
            StatusTextBlock.Foreground = (Brush)FindResource("SuccessBrush");
            OfflineActionPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("AccentBrush");
            StatusTextBlock.Text = "Lemonade Server unreachable (Offline)";
            StatusTextBlock.Foreground = (Brush)FindResource("AccentBrush");
            OfflineActionPanel.Visibility = Visibility.Visible;

            var cliFound = _dependencyService.IsLemonadeCliAvailable();
            StartServerButton.Visibility = cliFound ? Visibility.Visible : Visibility.Collapsed;
            InstallLemonadeButton.Visibility = !cliFound ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        try
        {
            await CheckConnectionAsync();
            await RefreshModelsAsync();
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void OnStartServerClick(object sender, RoutedEventArgs e)
    {
        StartServerButton.IsEnabled = false;
        StatusTextBlock.Text = "Starting Lemonade Server...";
        StatusDot.Fill = (Brush)FindResource("WarningBrush");

        try
        {
            var chatEndpoint = ChatEndpointTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(chatEndpoint))
                chatEndpoint = "http://localhost:13305/api/v1/chat/completions";
            var started = await _dependencyService.TryStartLemonadeServerAsync(chatEndpoint);
            if (started)
            {
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(1000);
                    var isConnected = await _ttsClient.TestConnectionAsync(EndpointTextBox.Text.Trim());
                    if (isConnected)
                    {
                        await CheckConnectionAsync();
                        await RefreshModelsAsync();
                        return;
                    }
                }
            }

            await CheckConnectionAsync();
        }
        finally
        {
            StartServerButton.IsEnabled = true;
        }
    }

    private async void OnInstallLemonadeClick(object sender, RoutedEventArgs e)
    {
        InstallLemonadeButton.IsEnabled = false;
        StatusTextBlock.Text = "Installing Lemonade Server via winget (please wait)...";
        StatusDot.Fill = (Brush)FindResource("WarningBrush");

        try
        {
            var installed = await _dependencyService.InstallViaWingetAsync();
            if (installed)
            {
                StatusTextBlock.Text = "Installation complete. Starting server...";
                await _dependencyService.TryStartLemonadeServerAsync(ChatEndpointTextBox.Text.Trim());
                await Task.Delay(3000);
                await CheckConnectionAsync();
                await RefreshModelsAsync();
            }
            else
            {
                MessageBox.Show(this, "Could not automatically install Lemonade Server via winget.\n\nPlease install manually by running in PowerShell:\nwinget install AMD.LemonadeServer\n\nOr download from https://lemonade-server.ai", "Installation Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                await CheckConnectionAsync();
            }
        }
        finally
        {
            InstallLemonadeButton.IsEnabled = true;
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
    }

    private async void OnPreviewVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_orchestrator.IsBusy)
        {
            _orchestrator.Stop();
            return;
        }

        var selectedVoiceItem = VoiceComboBox.SelectedItem as ComboBoxItem;
        var voice = selectedVoiceItem?.Tag?.ToString() ?? VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "af_heart";

        var settings = GetCurrentSettingsFromUi();
        settings.Voice = voice;

        await _orchestrator.SpeakTextAsync($"Hello! This is a preview of the {voice} voice with DirectML hardware acceleration.", settings);
    }

    private async void OnTestSpeechClick(object sender, RoutedEventArgs e)
    {
        if (_orchestrator.IsBusy)
        {
            _orchestrator.Stop();
            return;
        }

        var settings = GetCurrentSettingsFromUi();
        await _orchestrator.SpeakTextAsync("Dynamite TTS is active and ready. Highlight any text and press your shortcut to listen.", settings);
    }

    private void OnSpeedSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueTextBlock != null)
        {
            SpeedValueTextBlock.Text = $"{e.NewValue:F2}x";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var selectedEngine = (EngineComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "DirectML";
        var isDirectMl = string.Equals(selectedEngine, "DirectML", StringComparison.OrdinalIgnoreCase);

        var endpoint = EndpointTextBox.Text.Trim();
        if (!isDirectMl && string.IsNullOrWhiteSpace(endpoint))
        {
            MessageBox.Show(this, "Please enter a valid Lemonade Server endpoint URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            EndpointTextBox.Focus();
            return;
        }

        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "kokoro-v1";
        }

        var selectedVoiceItem = VoiceComboBox.SelectedItem as ComboBoxItem;
        var voice = selectedVoiceItem?.Tag?.ToString() ?? VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
        {
            voice = "af_heart";
        }

        var newHotkey = HotkeyCaptureControl.Hotkey ?? HotkeyConfig.Default;
        var modeToggleHotkey = ModeToggleHotkeyCaptureControl.Hotkey ?? HotkeyConfig.ModeToggleDefault;
        if (!_hotkeyService.TryRegisterPair(newHotkey, modeToggleHotkey, out var hotkeyError))
        {
            MessageBox.Show(this, $"{hotkeyError}\nPlease select a different shortcut key combination.", "Shortcut Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedDevice = AudioDeviceComboBox.SelectedItem as AudioDeviceInfo;
        var audioDeviceId = selectedDevice?.Id ?? string.Empty;

        var selectedDmlDevice = DirectMlDeviceComboBox.SelectedItem as AccelerationDeviceInfo;
        var dmlDeviceId = selectedDmlDevice?.DeviceId ?? -1;
        if (dmlDeviceId == -100 || selectedDmlDevice?.Kind == AccelerationDeviceKind.NpuUnavailable)
            dmlDeviceId = -1;

        var selectedPrecisionItem = DirectMlPrecisionComboBox.SelectedItem as ComboBoxItem;
        var precision = selectedPrecisionItem?.Tag?.ToString() ?? "float32";

        var isStartup = LaunchAtStartupCheckBox.IsChecked == true;
        _startupService.SetStartupEnabled(isStartup);

        var chatEndpoint = ChatEndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(chatEndpoint))
            chatEndpoint = "http://localhost:13305/api/v1/chat/completions";

        var newSettings = new AppSettings
        {
            SpeechEndpoint = endpoint,
            Model = model,
            Voice = voice,
            Speed = Math.Round(SpeedSlider.Value, 2),
            ResponseFormat = "mp3",
            Hotkey = newHotkey,
            ModeToggleHotkey = modeToggleHotkey,
            SpeakMode = GetSelectedSpeakMode(),
            ChatEndpoint = chatEndpoint,
            SummaryModel = SummaryModelComboBox.Text.Trim(),
            AudioDeviceId = audioDeviceId,
            PlaySoundOnStop = PlaySoundOnStopCheckBox.IsChecked == true,
            ShowStatusBubble = ShowStatusBubbleCheckBox.IsChecked == true,
            RunAtStartup = isStartup,
            EngineMode = selectedEngine,
            UseDirectMlAcceleration = true,
            DirectMlDeviceId = dmlDeviceId,
            DirectMlModelPrecision = precision,
            GpuIdleUnloadMinutes = _settingsStore.Current.GpuIdleUnloadMinutes
        };

        _settingsStore.Save(newSettings);
        Hide();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _orchestrator.Stop();
        PopulateFromSettings(_settingsStore.Current);
        Hide();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExplicitClose)
        {
            e.Cancel = true;
            _orchestrator.Stop();
            Hide();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!HotkeyCaptureControl.IsCapturing && !ModeToggleHotkeyCaptureControl.IsCapturing)
            {
                _orchestrator.Stop();
                PopulateFromSettings(_settingsStore.Current);
                Hide();
                e.Handled = true;
            }
        }
    }

    private void SelectSpeakMode(SpeakMode mode)
    {
        if (SpeakModeComboBox == null) return;
        foreach (ComboBoxItem item in SpeakModeComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                SpeakModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private SpeakMode GetSelectedSpeakMode()
    {
        var tag = (SpeakModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, nameof(SpeakMode.Summary), StringComparison.OrdinalIgnoreCase)
            ? SpeakMode.Summary
            : SpeakMode.Verbatim;
    }

    private async void OnRefreshSummaryModelsClick(object sender, RoutedEventArgs e)
    {
        await RefreshSummaryModelsAsync();
    }

    private async void OnEnsureSummaryModelClick(object sender, RoutedEventArgs e)
    {
        EnsureSummaryModelButton.IsEnabled = false;
        var endpoint = ChatEndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = "http://localhost:13305/api/v1/chat/completions";

        try
        {
            StatusTextBlock.Text = "Ensuring Lemonade summary model…";
            var progress = new Progress<string>(msg =>
            {
                Dispatcher.BeginInvoke(() => StatusTextBlock.Text = msg);
            });

            var preferred = SummaryModelComboBox.Text.Trim();
            var modelId = await _dependencyService.EnsureChatModelAsync(endpoint, preferred, progress);
            SummaryModelComboBox.Text = modelId;
            await RefreshSummaryModelsAsync();
            StatusTextBlock.Text = $"Summary model ready: {modelId}";
            _notificationService.ShowInfo("Summary model", $"Lemonade chat model '{modelId}' is ready.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Could not prepare summary model";
            MessageBox.Show(this, ex.Message, "Summary model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EnsureSummaryModelButton.IsEnabled = true;
        }
    }

    private async Task RefreshSummaryModelsAsync()
    {
        try
        {
            var endpoint = string.IsNullOrWhiteSpace(ChatEndpointTextBox.Text)
                ? _settingsStore.Current.ChatEndpoint
                : ChatEndpointTextBox.Text.Trim();

            var models = await _chatClient.GetModelsAsync(endpoint);
            var currentText = SummaryModelComboBox.Text;
            if (string.IsNullOrEmpty(currentText))
                currentText = _settingsStore.Current.SummaryModel;

            SummaryModelComboBox.Items.Clear();
            foreach (var model in models.Where(m => !LemonadeChatClient.IsLikelyTtsModel(m.Id)))
            {
                SummaryModelComboBox.Items.Add(model.Id);
            }

            foreach (var model in models.Where(m => LemonadeChatClient.IsLikelyTtsModel(m.Id)))
            {
                SummaryModelComboBox.Items.Add(model.Id);
            }

            if (!string.IsNullOrWhiteSpace(currentText))
            {
                SummaryModelComboBox.Text = currentText;
            }
            else
            {
                var picked = LemonadeChatClient.PickChatModel(models, preferred: null);
                if (!string.IsNullOrEmpty(picked))
                    SummaryModelComboBox.Text = picked;
            }
        }
        catch
        {
            // Non-fatal; leave the current text
        }
    }
}
