using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamiteTts.Models;
using DynamiteTts.Native;
using DynamiteTts.Services;
using Xunit;
using Xunit.Abstractions;

namespace DynamiteTts.Tests;

/// <summary>
/// Reproduces the real app path end to end: SpeechOrchestrator + local engine + WASAPI output on a
/// WPF dispatcher thread, with a state subscriber that marshals onto the UI thread the way the
/// Settings window does. Plays audio through the default output device. Tagged Perf.
/// </summary>
[Trait("Category", "Perf")]
public class EndToEndPlaybackHarnessTests
{
    private const string Paragraph =
        "The quick brown fox jumps over the lazy dog. This is a benchmark of local text to speech synthesis. " +
        "We want to know how long it takes before the first audio is heard, and whether playback starts while " +
        "the rest of the paragraph is still being rendered. A typical highlighted paragraph from a web page is " +
        "roughly this long, with a handful of sentences of varying length, some short, and some that run on for " +
        "a little while before finally coming to an end.";

    private readonly ITestOutputHelper _output;

    public EndToEndPlaybackHarnessTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(CudaDeviceService.AutoDeviceId)]    // GPU when available (first run bridges on CPU)
    [InlineData(CudaDeviceService.CpuOnlyDeviceId)] // CPU only
    public void LocalEngine_StartsPlaybackBeforeSynthesisFinishes(int deviceId)
    {
        RunOnDispatcher(async dispatcher =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.json");
            try
            {
                var store = new AppSettingsStore(settingsPath);
                var settings = store.Current;
                settings.EngineMode = "DirectML";
                settings.UseDirectMlAcceleration = true;
                settings.DirectMlDeviceId = deviceId;
                settings.DirectMlModelPrecision = "float32";
                settings.Voice = "am_adam";
                settings.Speed = 1.28;
                store.Save(settings);

                using var trayHost = new TrayIconHost();
                var engine = new LocalTtsService { AutoDownloadGpuRuntime = false };
                var audio = new AudioPlaybackService();
                var lemonade = new LemonadeTtsClient();
                var chat = new LemonadeChatClient();
                var orchestrator = new SpeechOrchestrator(
                    store, new ClipboardSelectionService(), lemonade, chat, engine, audio, trayHost, new TrayNotificationService(trayHost));

                var clock = Stopwatch.StartNew();
                orchestrator.StateChanged += state =>
                {
                    // Worst case for the pipeline: a subscriber that synchronously hops to the UI thread.
                    dispatcher.Invoke(() => { });
                    _output.WriteLine($"    {clock.ElapsedMilliseconds,6} ms  state -> {state} (thread {Environment.CurrentManagedThreadId})");
                };

                audio.WarmUp(settings.AudioDeviceId);
                await engine.InitializeAsync(true, deviceId, "float32");
                _output.WriteLine($"engine: {engine.State}: {engine.ActiveDeviceDescription}");

                for (int run = 1; run <= 2; run++)
                {
                    clock.Restart();
                    await orchestrator.SpeakTextAsync(Paragraph);
                    var m = orchestrator.LastMetrics!;
                    _output.WriteLine($"  run{run}: {m}");

                    Assert.False(m.UsedFallback, $"local streaming fell back: {m.FallbackReason}");
                    Assert.False(m.Cancelled);
                    Assert.True(m.Segments >= 3, "expected sentence-sized segments");
                    Assert.InRange(m.FirstAudioMs, 0, 2500);
                    Assert.True(m.FirstAudioMs < m.SynthesisDoneMs,
                        $"playback should start before synthesis finishes (first audible {m.FirstAudioMs:F0} ms, synthesis done {m.SynthesisDoneMs:F0} ms)");
                    Assert.Equal(TrayIconState.Idle, orchestrator.CurrentState);

                    await engine.WaitForGpuLoadAsync();
                }

                orchestrator.Dispose(); // also disposes the engine, audio output and HTTP client
            }
            finally
            {
                try { File.Delete(settingsPath); } catch { }
            }
        });
    }

    /// <summary>Same end-to-end path through the Phonon engine: orchestrator + dynamite_phonon.dll + WASAPI.</summary>
    [Fact]
    public void PhononEngine_StartsPlaybackBeforeSynthesisFinishes()
    {
        if (!PhononTtsService.IsRuntimePresent() || PhononTtsService.ResolveModelDirectory(null) == null)
        {
            _output.WriteLine("skipped: Phonon runtime or model not present next to the test binary");
            return;
        }

        RunOnDispatcher(async dispatcher =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.json");
            try
            {
                var store = new AppSettingsStore(settingsPath);
                var settings = store.Current;
                settings.EngineMode = AppSettings.EnginePhonon;
                settings.PhononVoice = "Easton";
                settings.PhononLanguage = "en";
                settings.Speed = 1.15;
                store.Save(settings);

                using var trayHost = new TrayIconHost();
                var kokoro = new LocalTtsService { AutoDownloadGpuRuntime = false };
                var phonon = new PhononTtsService();
                var audio = new AudioPlaybackService();
                var lemonade = new LemonadeTtsClient();
                var chat = new LemonadeChatClient();
                var orchestrator = new SpeechOrchestrator(
                    store, new ClipboardSelectionService(), lemonade, chat, kokoro, audio, trayHost, new TrayNotificationService(trayHost),
                    phononService: phonon);

                var clock = Stopwatch.StartNew();
                orchestrator.StateChanged += state =>
                {
                    dispatcher.Invoke(() => { });
                    _output.WriteLine($"    {clock.ElapsedMilliseconds,6} ms  state -> {state} (thread {Environment.CurrentManagedThreadId})");
                };
                phonon.StatusChanged += s => _output.WriteLine($"    phonon: {phonon.State}: {s}");

                audio.WarmUp(settings.AudioDeviceId);
                await phonon.InitializeAsync(settings);
                _output.WriteLine($"engine: {phonon.State}: {phonon.Description}");

                for (int run = 1; run <= 2; run++)
                {
                    clock.Restart();
                    await orchestrator.SpeakTextAsync(Paragraph);
                    var m = orchestrator.LastMetrics!;
                    _output.WriteLine($"  run{run}: {m}");

                    Assert.Equal("phonon", m.Engine);
                    Assert.False(m.UsedFallback, $"phonon streaming fell back: {m.FallbackReason}");
                    Assert.False(m.Cancelled);
                    Assert.True(m.Segments >= 3, "expected several audio blocks");
                    Assert.InRange(m.FirstAudioMs, 0, 1500);
                    Assert.True(m.FirstAudioMs < m.SynthesisDoneMs,
                        $"playback should start before synthesis finishes (first audible {m.FirstAudioMs:F0} ms, synthesis done {m.SynthesisDoneMs:F0} ms)");
                    Assert.True(m.UnderrunSeconds < 0.5, $"playback starved for {m.UnderrunSeconds:F2}s");
                    Assert.Equal(TrayIconState.Idle, orchestrator.CurrentState);
                }

                // Stop mid-utterance must leave the pipeline idle and reusable.
                clock.Restart();
                var speaking = orchestrator.SpeakTextAsync(Paragraph);
                await Task.Delay(1200);
                orchestrator.Stop();
                await speaking;
                _output.WriteLine($"  stop: {orchestrator.LastMetrics}");
                Assert.True(orchestrator.LastMetrics!.Cancelled);
                Assert.Equal(TrayIconState.Idle, orchestrator.CurrentState);

                orchestrator.Dispose();
            }
            finally
            {
                try { File.Delete(settingsPath); } catch { }
            }
        });
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

        if (!thread.Join(TimeSpan.FromMinutes(3)))
            throw new TimeoutException("Dispatcher test did not finish within 3 minutes.");
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
