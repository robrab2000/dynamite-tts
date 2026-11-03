using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamiteTts.Models;
using DynamiteTts.Native;
using DynamiteTts.Services;
using DynamiteTts.UI;
using Xunit;
using Xunit.Abstractions;

namespace DynamiteTts.Tests;

/// <summary>
/// Opens the real Settings window (with the app's resource dictionary) on a WPF dispatcher, switches
/// the engine picker to Phonon, and checks the Phonon panel and voice list follow. Saves screenshots
/// to experiments/settings-shots. Puts a window on the desktop, so it is tagged Perf.
/// </summary>
[Trait("Category", "Perf")]
public class SettingsWindowVisualTests
{
    private readonly ITestOutputHelper _output;

    public SettingsWindowVisualTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EnginePicker_SwitchesToPhonon_ShowsPanelAndVoices()
    {
        var shots = Path.Combine(FindRepoRoot(AppContext.BaseDirectory) ?? Path.GetTempPath(), "experiments", "settings-shots");
        Directory.CreateDirectory(shots);

        RunOnDispatcher(async dispatcher =>
        {
            // Relative pack URIs (the window icon) resolve against the entry assembly, which is the test host here.
            try { Application.ResourceAssembly = typeof(SettingsWindow).Assembly; }
            catch (InvalidOperationException)
            {
                typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .SetValue(null, typeof(SettingsWindow).Assembly);
            }

            // The App's resources (brushes, button styles) without its OnStartup (single-instance mutex etc.):
            // constructing App and calling InitializeComponent loads App.xaml; only Run() would raise Startup.
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            var settingsPath = Path.Combine(Path.GetTempPath(), $"settings_ui_{Guid.NewGuid():N}.json");
            var store = new AppSettingsStore(settingsPath);
            // The exercise starts on Kokoro and switches to Phonon. Seed that, because a fresh
            // settings file now defaults to Phonon on an AVX2 machine.
            var seeded = store.Current;
            seeded.EngineMode = AppSettings.EngineKokoro;
            store.Save(seeded);
            using var trayHost = new TrayIconHost();
            var kokoro = new LocalTtsService { AutoDownloadGpuRuntime = false };
            var phonon = new PhononTtsService();
            var audio = new AudioPlaybackService();
            var lemonade = new LemonadeTtsClient();
            var chat = new LemonadeChatClient();
            var dependency = new LemonadeDependencyService(chat);
            using var orchestrator = new SpeechOrchestrator(
                store, new ClipboardSelectionService(), lemonade, chat, kokoro, audio, trayHost, new TrayNotificationService(trayHost),
                dependency, phonon);
            using var hotkeys = new HotkeyService(trayHost.Handle);

            SettingsWindow window;
            try
            {
                window = new SettingsWindow(store, lemonade, chat, hotkeys, new CudaDeviceService(), new AudioDeviceService(),
                    new StartupRegistrationService(), dependency, orchestrator, new TrayNotificationService(trayHost));
            }
            catch (Exception ex)
            {
                // A XAML failure leaves a half-built window registered with the Application, whose shutdown then
                // closes it and crashes in its Closing handler before xUnit can report anything: persist the cause.
                File.WriteAllText(Path.Combine(shots, "construction-error.txt"), ex.ToString());
                foreach (Window w in Application.Current!.Windows)
                {
                    var handler = (System.ComponentModel.CancelEventHandler)Delegate.CreateDelegate(
                        typeof(System.ComponentModel.CancelEventHandler), w,
                        typeof(SettingsWindow).GetMethod("OnWindowClosing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!);
                    w.Closing -= handler;
                }
                throw;
            }

            try
            {
                await Exercise(window, dispatcher, shots);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(shots, "assertion-error.txt"), ex.ToString());
                throw;
            }
            finally
            {
                // OnLoaded is still probing Lemonade (async void); let it finish before the window and the
                // Application go away, or its FindResource continuation faults the dispatcher on shutdown.
                var statusText = (TextBlock)window.FindName("StatusTextBlock")!;
                for (int i = 0; i < 60 && statusText.Text.Contains("connection...", StringComparison.OrdinalIgnoreCase); i++)
                    await Task.Delay(250);
                await Idle(dispatcher);
                // Close explicitly while the orchestrator is still alive: Application shutdown would otherwise
                // close the window through OnWindowClosing, which calls into the (by then disposed) orchestrator.
                window.ShutdownWindow();
                try { File.Delete(settingsPath); } catch { }
            }
        });
    }

    private async Task Exercise(SettingsWindow window, Dispatcher dispatcher, string shots)
    {
        // App shutdown during the exercise must not raise the unsaved-changes dialog; that modal blocks the dispatcher.
        typeof(SettingsWindow).GetField("_leaveInProgress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(window, true);
        window.ShowSettings();
        await Idle(dispatcher);
        await Task.Delay(500);

        var engineBox = (ComboBox)window.FindName("EngineComboBox")!;
        var phononPanel = (FrameworkElement)window.FindName("PhononInfoPanel")!;
        var kokoroPanel = (FrameworkElement)window.FindName("DirectMlInfoPanel")!;
        var voiceBox = (ComboBox)window.FindName("VoiceComboBox")!;
        var statusTitle = (TextBlock)window.FindName("PhononStatusTitle")!;

        Assert.Equal(3, engineBox.Items.Count);
        Assert.Equal(Visibility.Collapsed, phononPanel.Visibility);
        Assert.Contains(voiceBox.Items.OfType<ComboBoxItem>(), i => Equals(i.Tag, "am_adam"));
        AssertChoiceListsAreClosed(window, voiceBox);
        Save(window, Path.Combine(shots, "1-kokoro.png"));

        engineBox.SelectedItem = engineBox.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, AppSettings.EnginePhonon));
        await Idle(dispatcher);

        Assert.Equal(Visibility.Visible, phononPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, kokoroPanel.Visibility);
        var voices = voiceBox.Items.OfType<ComboBoxItem>().Select(i => i.Tag?.ToString()).ToList();
        _output.WriteLine($"phonon voices: {voices.Count} (first: {string.Join(", ", voices.Take(5))})");
        Assert.Equal(PhononVoice.DefaultId, voices[0]);
        Assert.Contains("Easton", voices);
        Assert.DoesNotContain("am_adam", voices);
        Assert.Equal(PhononVoice.DefaultId, (voiceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        AssertClosedCombo(voiceBox);
        _output.WriteLine($"phonon status: {statusTitle.Text}");
        Save(window, Path.Combine(shots, "2-phonon.png"));

        // Switching back restores the Kokoro voices.
        engineBox.SelectedItem = engineBox.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, AppSettings.EngineKokoro));
        await Idle(dispatcher);
        Assert.Equal(Visibility.Collapsed, phononPanel.Visibility);
        Assert.Contains(voiceBox.Items.OfType<ComboBoxItem>(), i => Equals(i.Tag, "af_heart"));
    }

    private static void AssertChoiceListsAreClosed(SettingsWindow window, ComboBox voiceBox)
    {
        AssertClosedCombo(voiceBox);
        AssertClosedCombo((ComboBox)window.FindName("ModelComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("ChatModelComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("EngineComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("PhononLanguageComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("DirectMlDeviceComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("DirectMlPrecisionComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("AudioDeviceComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("SpeakModeComboBox")!);
        AssertClosedCombo((ComboBox)window.FindName("ExplainAudienceComboBox")!);

        var selectedVoice = (voiceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        Assert.False(string.IsNullOrEmpty(selectedVoice));
        Assert.Contains(voiceBox.Items.OfType<ComboBoxItem>(), i => Equals(i.Tag, selectedVoice));
    }

    private static void AssertClosedCombo(ComboBox box)
    {
        Assert.False(box.IsEditable);
        // The dark-theme template swaps in a text box only when IsEditable is true.
        var editor = box.Template?.FindName("PART_EditableTextBox", box) as TextBox;
        if (editor != null)
            Assert.Equal(Visibility.Hidden, editor.Visibility);
    }

    private static Task Idle(Dispatcher dispatcher) => dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private void Save(Window window, string path)
    {
        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        _output.WriteLine($"window size {w}x{h}, visible {window.IsVisible}");
        if (w <= 0 || h <= 0) return;
        // Rendering the Window itself gives a blank image when it is not on screen; render its content tree
        // over the window background instead.
        var content = (Visual)window.Content;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        _output.WriteLine($"saved {path}");
    }

    private static void RunOnDispatcher(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(dispatcher); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("Dispatcher test did not finish within 2 minutes.");
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
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
