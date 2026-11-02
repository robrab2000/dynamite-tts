using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;
using Xunit.Abstractions;

namespace DynamiteTts.Tests;

/// <summary>
/// Drives the real Phonon engine (dynamite_phonon.dll + the phonon-31m-en checkpoint next to the
/// test binary, both copied by the app project when present) and reports timings. Each test
/// returns early with a note when the runtime or the model is not on this machine, so the suite
/// still passes on a CI runner that has neither; the timing cases are also tagged Perf.
/// </summary>
public class PhononStreamingHarnessTests
{
    private const string SampleText =
        "The quick brown fox jumps over the lazy dog. This is a benchmark of local text to speech synthesis. " +
        "We want to know how long it takes before the first audio segment is ready, and how long the whole " +
        "paragraph takes to render. A typical highlighted paragraph from a web page is roughly this long, " +
        "with a handful of sentences of varying length, some short, and some that run on for a little while " +
        "before finally coming to an end. That should be enough text to make the numbers meaningful.";

    private readonly ITestOutputHelper _output;

    public PhononStreamingHarnessTests(ITestOutputHelper output) => _output = output;

    private bool Available(out string reason)
    {
        if (!PhononTtsService.IsRuntimePresent()) { reason = "dynamite_phonon.dll is not next to the test binary"; return false; }
        if (PhononTtsService.ResolveModelDirectory(null) == null) { reason = "phonon-31m-en is not bundled or cached on this machine"; return false; }
        reason = string.Empty;
        return true;
    }

    [Fact]
    public async Task Loads_Speaks_AndStreamsFirstAudioQuickly()
    {
        if (!Available(out var why)) { _output.WriteLine("skipped: " + why); return; }

        using var service = new PhononTtsService();
        service.StatusChanged += s => _output.WriteLine($"    [status] {service.State}: {s}");

        var sw = Stopwatch.StartNew();
        await service.InitializeAsync(null, "en");
        _output.WriteLine($"init+warmup {sw.ElapsedMilliseconds} ms -> {service.State}: {service.Description} (threads {service.Threads})");
        Assert.Equal(PhononEngineState.Ready, service.State);
        Assert.Contains("default", service.LoadedVoices);

        var (first, total, blocks, audio) = await RunOnce(service, "default", 1.0, SampleText);
        Assert.True(audio > 10, $"expected a paragraph of audio, got {audio:F1}s");
        Assert.True(first < 1500, $"first block took {first:F0} ms");
        Assert.True(total < audio * 1000, $"slower than realtime: {total:F0} ms for {audio:F1}s");
        Assert.True(blocks >= 3, "expected the audio to arrive in several blocks, not one");
    }

    [Fact]
    public async Task Speed_ChangesAudioDuration_WithoutReload()
    {
        if (!Available(out var why)) { _output.WriteLine("skipped: " + why); return; }

        using var service = new PhononTtsService();
        await service.InitializeAsync(null, "en");
        const string text = "This sentence is spoken at three different speeds, so the durations should differ.";

        var slow = (await RunOnce(service, "default", 0.8, text)).audio;
        var normal = (await RunOnce(service, "default", 1.0, text)).audio;
        var fast = (await RunOnce(service, "default", 1.4, text)).audio;
        _output.WriteLine($"durations: slow {slow:F2}s, normal {normal:F2}s, fast {fast:F2}s");
        Assert.True(fast < normal, "1.4x should be shorter than 1.0x");
        Assert.True(normal < slow, "1.0x should be shorter than 0.8x");
    }

    [Fact]
    public async Task Cancel_StopsGenerationPromptly()
    {
        if (!Available(out var why)) { _output.WriteLine("skipped: " + why); return; }

        using var service = new PhononTtsService();
        await service.InitializeAsync(null, "en");

        using var cts = new CancellationTokenSource();
        var blocks = 0;
        var sw = Stopwatch.StartNew();
        var task = service.GenerateSpeechStreamingAsync(new[] { SampleText, SampleText }, "default", 1.0, null, "en",
            samples =>
            {
                if (++blocks == 2) cts.Cancel();
                return Task.CompletedTask;
            }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        _output.WriteLine($"cancelled after {blocks} blocks in {sw.ElapsedMilliseconds} ms");
        Assert.True(blocks <= 4, $"generation kept going after cancel ({blocks} blocks)");

        // The engine must be usable again straight away.
        var (first, _, _, audio) = await RunOnce(service, "default", 1.0, "Still working after a cancel.");
        Assert.True(audio > 0.5);
        _output.WriteLine($"follow-up first block {first:F0} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task CatalogVoice_DownloadsAndSpeaks()
    {
        if (!Available(out var why)) { _output.WriteLine("skipped: " + why); return; }

        using var service = new PhononTtsService();
        await service.InitializeAsync(null, "en");
        var (first, total, _, audio) = await RunOnce(service, "Easton", 1.0, "Hello from Easton, one of the catalogue voices.");
        Assert.Contains("Easton", service.LoadedVoices);
        Assert.True(audio > 1);
        _output.WriteLine($"Easton: first {first:F0} ms, total {total:F0} ms for {audio:F1}s");

        // Unknown voices fall back to the default instead of failing the utterance.
        var fallback = await RunOnce(service, "NoSuchVoice", 1.0, "An unknown voice still speaks.");
        Assert.True(fallback.audio > 0.5);
    }

    [Theory]
    [Trait("Category", "Perf")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task Threads_ReportThroughput(int threads)
    {
        if (!Available(out var why)) { _output.WriteLine("skipped: " + why); return; }

        // xn's thread count is process-wide and set once, so each thread count needs its own process to
        // be measured cleanly; within one test run only the first value takes effect. Reported anyway.
        using var service = new PhononTtsService { Threads = threads };
        await service.InitializeAsync(null, "en");
        for (int run = 1; run <= 2; run++)
        {
            var (first, total, blocks, audio) = await RunOnce(service, "default", 1.0, SampleText);
            _output.WriteLine($"[threads {threads}] run {run}: first {first:F0} ms, total {total:F0} ms, {audio:F1}s audio in {blocks} blocks = {audio * 1000 / total:F1}x realtime");
        }
    }

    private async Task<(double firstMs, double totalMs, int blocks, double audio)> RunOnce(PhononTtsService service, string voice, double speed, string text)
    {
        var sw = Stopwatch.StartNew();
        double first = -1;
        var blocks = 0;
        long samples = 0;
        await service.GenerateSpeechStreamingAsync(new[] { text }, voice, speed, null, "en", s =>
        {
            if (first < 0) first = sw.Elapsed.TotalMilliseconds;
            blocks++;
            samples += s.Length;
            return Task.CompletedTask;
        });
        var total = sw.Elapsed.TotalMilliseconds;
        var audio = samples / (double)PhononTtsService.SampleRate;
        _output.WriteLine($"  {text.Length} chars @ {speed:F2}x ({voice}): first {first:F0} ms, total {total:F0} ms, {audio:F1}s audio in {blocks} blocks = {audio * 1000 / total:F1}x realtime");
        return (first, total, blocks, audio);
    }
}
