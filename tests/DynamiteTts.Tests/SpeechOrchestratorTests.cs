using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Native;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class SpeechOrchestratorTests : IDisposable
{
    private readonly string _tempSettingsPath;

    public SpeechOrchestratorTests()
    {
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"orch_test_{Guid.NewGuid()}.json");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task SpeakTextAsync_CallsClientAndAudioService()
    {
        var settingsStore = new AppSettingsStore(_tempSettingsPath);
        var initialSettings = settingsStore.Current;
        initialSettings.EngineMode = "LemonadeServer";
        settingsStore.Save(initialSettings);

        var clipboardService = new ClipboardSelectionService();
        var audioService = new AudioPlaybackService();
        using var localTtsService = new LocalTtsService();

        var wavData = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'e', (byte)'f', (byte)'m', (byte)'t', (byte)' ', 16, 0, 0, 0, 1, 0, 1, 0, 0x80, 0x3E, 0, 0, 0x00, 0x7D, 0, 0, 2, 0, 16, 0, (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0, 0, 0, 0 };
        
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavData)
            }
        };

        using var httpClient = new HttpClient(handler);
        using var ttsClient = new LemonadeTtsClient(httpClient);
        using var trayHost = new TrayIconHost();
        var notificationService = new TrayNotificationService(trayHost);

        using var orchestrator = new SpeechOrchestrator(
            settingsStore,
            clipboardService,
            ttsClient,
            localTtsService,
            audioService,
            trayHost,
            notificationService);

        var observedStates = new List<TrayIconState>();
        orchestrator.StateChanged += state => observedStates.Add(state);

        await orchestrator.SpeakTextAsync("Hello world");
        Assert.False(orchestrator.IsBusy);
        Assert.Equal(TrayIconState.Idle, orchestrator.CurrentState);
        Assert.Contains(TrayIconState.Synthesizing, observedStates);
        Assert.Contains(TrayIconState.Speaking, observedStates);
        Assert.Contains(TrayIconState.Idle, observedStates);
    }

    [Fact]
    public async Task SpeakTextAsync_WithOverrideSettings_PassesCustomSpeedAndVoice()
    {
        var settingsStore = new AppSettingsStore(_tempSettingsPath);
        var clipboardService = new ClipboardSelectionService();
        var audioService = new AudioPlaybackService();
        using var localTtsService = new LocalTtsService();

        var wavData = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'e', (byte)'f', (byte)'m', (byte)'t', (byte)' ', 16, 0, 0, 0, 1, 0, 1, 0, 0x80, 0x3E, 0, 0, 0x00, 0x7D, 0, 0, 2, 0, 16, 0, (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0, 0, 0, 0 };
        
        double capturedSpeed = 0;
        string? capturedVoice = null;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var json = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("speed", out var sProp))
                    {
                        capturedSpeed = sProp.GetDouble();
                    }
                    if (doc.RootElement.TryGetProperty("voice", out var vProp))
                    {
                        capturedVoice = vProp.GetString();
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(wavData)
                };
            }
        };

        using var httpClient = new HttpClient(handler);
        using var ttsClient = new LemonadeTtsClient(httpClient);
        using var trayHost = new TrayIconHost();
        var notificationService = new TrayNotificationService(trayHost);

        using var orchestrator = new SpeechOrchestrator(
            settingsStore,
            clipboardService,
            ttsClient,
            localTtsService,
            audioService,
            trayHost,
            notificationService);

        var customSettings = new AppSettings
        {
            EngineMode = "LemonadeServer",
            Speed = 1.75,
            Voice = "fenrir"
        };

        await orchestrator.SpeakTextAsync("Testing custom speed", customSettings);

        Assert.Equal(1.75, capturedSpeed);
        Assert.Equal("fenrir", capturedVoice);
    }

    [Fact]
    public void Stop_WhenIdle_DoesNotThrow()
    {
        var settingsStore = new AppSettingsStore(_tempSettingsPath);
        var clipboardService = new ClipboardSelectionService();
        var audioService = new AudioPlaybackService();
        using var ttsClient = new LemonadeTtsClient();
        using var localTtsService = new LocalTtsService();
        using var trayHost = new TrayIconHost();
        var notificationService = new TrayNotificationService(trayHost);

        using var orchestrator = new SpeechOrchestrator(
            settingsStore,
            clipboardService,
            ttsClient,
            localTtsService,
            audioService,
            trayHost,
            notificationService);

        var ex = Record.Exception(() => orchestrator.Stop());
        Assert.Null(ex);
        Assert.Equal(TrayIconState.Idle, orchestrator.CurrentState);
    }

    [Fact]
    public void PlayStopChime_DoesNotThrow()
    {
        var audioService = new AudioPlaybackService();
        audioService.WarmUp(null);

        var ex = Record.Exception(() => audioService.PlayStopChime());
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempSettingsPath))
            {
                File.Delete(_tempSettingsPath);
            }
        }
        catch { }
    }
}
