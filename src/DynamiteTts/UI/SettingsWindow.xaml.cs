using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
        PopulateFromSettings(_settingsStore.Current);
        await RefreshModelsAsync();
        await CheckConnectionAsync();
    }

    public void ShowSettings()
    {
        PopulateVoices();
        RefreshAudioDevices();
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
            VoiceComboBox.Items.Add(preset.Id);
        }

        VoiceComboBox.Text = !string.IsNullOrEmpty(currentVoice) ? currentVoice : "coral";
        VoiceComboBox.SelectedItem = VoiceComboBox.Text;
    }

    private void RefreshAudioDevices()
    {
        var devices = _audioDeviceService.GetOutputDevices();
        AudioDeviceComboBox.ItemsSource = devices;
        AudioDeviceComboBox.DisplayMemberPath = nameof(AudioDeviceInfo.Name);
        AudioDeviceComboBox.SelectedValuePath = nameof(AudioDeviceInfo.Id);

        var currentDeviceId = _settingsStore.Current.AudioDeviceId;
        var selected = devices.FirstOrDefault(d => d.Id == currentDeviceId) ?? devices.FirstOrDefault();
        if (selected != null)
        {
            AudioDeviceComboBox.SelectedItem = selected;
        }
    }

    private void PopulateFromSettings(AppSettings settings)
    {
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

    private AppSettings GetCurrentSettingsFromUi()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = _settingsStore.Current.SpeechEndpoint;

        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = _settingsStore.Current.Model;

        var voice = VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = _settingsStore.Current.Voice;

        var selectedDevice = AudioDeviceComboBox.SelectedItem as AudioDeviceInfo;
        var audioDeviceId = selectedDevice?.Id ?? _settingsStore.Current.AudioDeviceId;

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
            RunAtStartup = LaunchAtStartupCheckBox.IsChecked == true
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

        var voice = VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "coral";

        var settings = GetCurrentSettingsFromUi();
        settings.Voice = voice;

        await _orchestrator.SpeakTextAsync($"Hello! This is a preview of the {voice} voice on Lemonade Server.", settings);
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
        var endpoint = EndpointTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            MessageBox.Show(this, "Please enter a valid Lemonade Server endpoint URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            EndpointTextBox.Focus();
            return;
        }

        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show(this, "Please specify a TTS model name (e.g. kokoro-v1).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ModelComboBox.Focus();
            return;
        }

        var voice = VoiceComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(voice))
        {
            voice = "coral";
        }

        var newHotkey = HotkeyCaptureControl.Hotkey ?? HotkeyConfig.Default;
        if (!_hotkeyService.TryRegister(newHotkey, out var hotkeyError))
        {
            MessageBox.Show(this, $"{hotkeyError}\nPlease select a different shortcut key combination.", "Shortcut Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedDevice = AudioDeviceComboBox.SelectedItem as AudioDeviceInfo;
        var audioDeviceId = selectedDevice?.Id ?? string.Empty;

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
            RunAtStartup = isStartup
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
            PopulateFromSettings(_settingsStore.Current);
            Hide();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyCaptureControl.IsFocused)
        {
            e.Handled = true;
            _orchestrator.Stop();
            PopulateFromSettings(_settingsStore.Current);
            Hide();
        }
    }
}
