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
    private readonly HotkeyService _hotkeyService;
    private readonly DirectMlDeviceService _directMlDeviceService;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly StartupRegistrationService _startupService;
    private readonly LemonadeDependencyService _dependencyService;
    private readonly SpeechOrchestrator _orchestrator;
    private readonly TrayNotificationService _notificationService;

    private bool _isExplicitClose;

    public SettingsWindow(
        AppSettingsStore settingsStore,
        LemonadeTtsClient ttsClient,
        HotkeyService hotkeyService,
        DirectMlDeviceService directMlDeviceService,
        AudioDeviceService audioDeviceService,
        StartupRegistrationService startupService,
        LemonadeDependencyService dependencyService,
        SpeechOrchestrator orchestrator,
        TrayNotificationService notificationService)
    {
        InitializeComponent();

        _settingsStore = settingsStore;
        _ttsClient = ttsClient;
        _hotkeyService = hotkeyService;
        _directMlDeviceService = directMlDeviceService;
        _audioDeviceService = audioDeviceService;
        _startupService = startupService;
        _dependencyService = dependencyService;
        _orchestrator = orchestrator;
        _notificationService = notificationService;

        _orchestrator.StateChanged += OnOrchestratorStateChanged;

        Loaded += OnLoaded;
    }

    private void OnOrchestratorStateChanged(TrayIconState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
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
        await RefreshModelsAsync();
        await CheckConnectionAsync();
    }

    public void ShowSettings()
    {
        PopulateVoices();
        RefreshAudioDevices();
        RefreshDirectMlDevices();
        PopulateFromSettings(_settingsStore.Current);
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
        var devices = _directMlDeviceService.GetAvailableDevices();
        DirectMlDeviceComboBox.ItemsSource = devices;
        DirectMlDeviceComboBox.DisplayMemberPath = "Name";
        DirectMlDeviceComboBox.SelectedValuePath = "DeviceId";

        var currentDeviceId = _settingsStore.Current.DirectMlDeviceId;
        var selected = devices.FirstOrDefault(d =>
                            d.DeviceId == currentDeviceId &&
                            d.Kind != DirectMlDeviceKind.NpuUnavailable)
                        ?? PreferFastestGpu(devices)
                        ?? devices.FirstOrDefault(d => d.Kind != DirectMlDeviceKind.NpuUnavailable);

        if (selected != null)
        {
            DirectMlDeviceComboBox.SelectedItem = selected;
        }
    }

    private static DirectMlDeviceInfo? PreferFastestGpu(IReadOnlyList<DirectMlDeviceInfo> devices)
    {
        return devices.FirstOrDefault(d =>
                   d.Kind == DirectMlDeviceKind.Gpu &&
                   (d.Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    d.Description.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    d.Description.Contains("RTX", StringComparison.OrdinalIgnoreCase)))
               ?? devices.FirstOrDefault(d =>
                   d.Kind == DirectMlDeviceKind.Gpu &&
                   (d.Description.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    d.Description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)));
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

        // DirectML Device selection
        if (DirectMlDeviceComboBox.ItemsSource is System.Collections.Generic.IEnumerable<DirectMlDeviceInfo> dmlDevices)
        {
            var match = dmlDevices.FirstOrDefault(d =>
                            d.DeviceId == settings.DirectMlDeviceId &&
                            d.Kind != DirectMlDeviceKind.NpuUnavailable)
                        ?? PreferFastestGpu(dmlDevices.ToList())
                        ?? dmlDevices.FirstOrDefault(d => d.Kind != DirectMlDeviceKind.NpuUnavailable);
            if (match != null)
            {
                DirectMlDeviceComboBox.SelectedItem = match;
            }
        }

        EndpointTextBox.Text = settings.SpeechEndpoint;
        ModelComboBox.Text = settings.Model;
        ModelComboBox.SelectedItem = settings.Model;

        VoiceComboBox.Text = settings.Voice;
        VoiceComboBox.SelectedItem = settings.Voice;

        SpeedSlider.Value = settings.Speed;
        SpeedValueTextBlock.Text = $"{settings.Speed:F2}x";
        HotkeyCaptureControl.Hotkey = settings.Hotkey;
        PlaySoundOnStopCheckBox.IsChecked = settings.PlaySoundOnStop;
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
        if (DirectMlDeviceComboBox?.SelectedItem is not DirectMlDeviceInfo deviceInfo)
            return;

        if (DirectMlStatusTitle == null || DirectMlStatusDetails == null || DirectMlStatusDot == null)
            return;

        if (deviceInfo.Kind == DirectMlDeviceKind.NpuUnavailable)
        {
            DirectMlStatusTitle.Text = "AMD Ryzen AI NPU unavailable for Kokoro TTS";
            DirectMlStatusTitle.Foreground = (Brush)FindResource("AccentBrush");
            DirectMlStatusDot.Fill = (Brush)FindResource("AccentBrush");
            DirectMlStatusDetails.Text = DirectMlDeviceService.NpuUnavailableReason +
                " Watch GPU 0/1 Compute in Task Manager when testing speech.";

            // Snap selection back to a usable GPU (prefer AMD iGPU if present)
            var devices = _directMlDeviceService.GetAvailableDevices();
            var amdGpu = devices.FirstOrDefault(d =>
                d.Kind == DirectMlDeviceKind.Gpu &&
                (d.Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                 d.Description.Contains("RTX", StringComparison.OrdinalIgnoreCase)))
                ?? devices.FirstOrDefault(d =>
                d.Kind == DirectMlDeviceKind.Gpu &&
                d.Description.Contains("AMD", StringComparison.OrdinalIgnoreCase));
            var fallback = amdGpu
                ?? devices.FirstOrDefault(d => d.Kind == DirectMlDeviceKind.Gpu)
                ?? devices.FirstOrDefault(d => d.Kind == DirectMlDeviceKind.Auto);

            if (fallback != null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show(this,
                        DirectMlDeviceService.NpuUnavailableReason +
                        "\n\nSwitching you to: " + fallback.Name,
                        "NPU not available for TTS",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DirectMlDeviceComboBox.SelectedItem = fallback;
                });
            }

            return;
        }

        if (deviceInfo.Kind == DirectMlDeviceKind.Auto || deviceInfo.DeviceId == -1)
        {
            DirectMlStatusTitle.Text = "DirectML GPU acceleration (Auto → fastest GPU)";
            DirectMlStatusTitle.Foreground = (Brush)FindResource("SuccessBrush");
            DirectMlStatusDot.Fill = (Brush)FindResource("SuccessBrush");
            DirectMlStatusDetails.Text =
                "Prefers your NVIDIA RTX when present. Audio now streams segment-by-segment so speech starts sooner. Watch that GPU's Compute graph — not the NPU.";
            return;
        }

        DirectMlStatusTitle.Text = $"DirectML GPU: {deviceInfo.Description}";
        DirectMlStatusTitle.Foreground = (Brush)FindResource("SuccessBrush");
        DirectMlStatusDot.Fill = (Brush)FindResource("SuccessBrush");
        DirectMlStatusDetails.Text =
            $"Kokoro streams short segments on DXGI adapter {deviceInfo.DeviceId} for lower latency. Watch that GPU's Compute engine in Task Manager.";
    }

    private void OnRefreshAdaptersClick(object sender, RoutedEventArgs e)
    {
        RefreshDirectMlDevices();
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

        var selectedDmlDevice = DirectMlDeviceComboBox.SelectedItem as DirectMlDeviceInfo;
        var dmlDeviceId = selectedDmlDevice?.DeviceId ?? _settingsStore.Current.DirectMlDeviceId;
        if (dmlDeviceId == -100 || selectedDmlDevice?.Kind == DirectMlDeviceKind.NpuUnavailable)
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
            AudioDeviceId = audioDeviceId,
            PlaySoundOnStop = PlaySoundOnStopCheckBox.IsChecked == true,
            RunAtStartup = LaunchAtStartupCheckBox.IsChecked == true,
            EngineMode = selectedEngine,
            UseDirectMlAcceleration = true,
            DirectMlDeviceId = dmlDeviceId,
            DirectMlModelPrecision = precision
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
            var started = await _dependencyService.TryStartLemonadeServerAsync();
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
                await _dependencyService.TryStartLemonadeServerAsync();
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
        if (!_hotkeyService.TryRegister(newHotkey, out var hotkeyError))
        {
            MessageBox.Show(this, $"{hotkeyError}\nPlease select a different shortcut key combination.", "Shortcut Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedDevice = AudioDeviceComboBox.SelectedItem as AudioDeviceInfo;
        var audioDeviceId = selectedDevice?.Id ?? string.Empty;

        var selectedDmlDevice = DirectMlDeviceComboBox.SelectedItem as DirectMlDeviceInfo;
        var dmlDeviceId = selectedDmlDevice?.DeviceId ?? -1;
        if (dmlDeviceId == -100 || selectedDmlDevice?.Kind == DirectMlDeviceKind.NpuUnavailable)
            dmlDeviceId = -1;

        var selectedPrecisionItem = DirectMlPrecisionComboBox.SelectedItem as ComboBoxItem;
        var precision = selectedPrecisionItem?.Tag?.ToString() ?? "float16";

        var isStartup = LaunchAtStartupCheckBox.IsChecked == true;
        _startupService.SetStartupEnabled(isStartup);

        var newSettings = new AppSettings
        {
            SpeechEndpoint = endpoint,
            Model = model,
            Voice = voice,
            Speed = Math.Round(SpeedSlider.Value, 2),
            ResponseFormat = "mp3",
            Hotkey = newHotkey,
            AudioDeviceId = audioDeviceId,
            PlaySoundOnStop = PlaySoundOnStopCheckBox.IsChecked == true,
            RunAtStartup = isStartup,
            EngineMode = selectedEngine,
            UseDirectMlAcceleration = true,
            DirectMlDeviceId = dmlDeviceId,
            DirectMlModelPrecision = precision
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
            if (!HotkeyCaptureControl.IsCapturing)
            {
                _orchestrator.Stop();
                PopulateFromSettings(_settingsStore.Current);
                Hide();
                e.Handled = true;
            }
        }
    }
}
