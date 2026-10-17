using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class AudioPlaybackServiceTests : IDisposable
{
    private readonly AudioPlaybackService _service;

    public AudioPlaybackServiceTests()
    {
        _service = new AudioPlaybackService();
    }

    private static byte[] CreateSilentWav(int durationMs = 100, int sampleRate = 16000)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        int channels = 1;
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        int totalSamples = (sampleRate * durationMs) / 1000;
        int dataSize = totalSamples * blockAlign;

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // SubChunk1Size
        writer.Write((short)1); // AudioFormat PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            writer.Write((short)0);
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task PlayAsync_EmptyBytes_CompletesImmediatelyWithoutError()
    {
        using var cts = new CancellationTokenSource();
        await _service.PlayAsync(Array.Empty<byte>(), null, cts.Token);
        Assert.False(_service.IsPlaying);
    }

    [Fact]
    public void Stop_WhenNotPlaying_DoesNotThrow()
    {
        var ex = Record.Exception(() => _service.Stop());
        Assert.Null(ex);
    }

    [Fact]
    public async Task PlayAsync_ShortWav_CompletesNaturally()
    {
        var wavBytes = CreateSilentWav(durationMs: 50, sampleRate: 24000);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await _service.PlayAsync(wavBytes, null, cts.Token);
        Assert.False(_service.IsPlaying);
    }

    [Fact]
    public async Task PlayAsync_RealMp3_CompletesNaturally()
    {
        var rootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var mp3Path = Path.Combine(rootDir, "sample_preview.mp3");
        Assert.True(File.Exists(mp3Path), $"MP3 file should exist at: {mp3Path}");

        var mp3Bytes = await File.ReadAllBytesAsync(mp3Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await _service.PlayAsync(mp3Bytes, null, cts.Token);
        Assert.False(_service.IsPlaying);
    }

    [Fact]
    public async Task PlayAsync_ValidWav_CanBeCancelledPromptly()
    {
        var wavBytes = CreateSilentWav(durationMs: 2000);
        using var cts = new CancellationTokenSource();

        // Cancel after 50ms
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _service.PlayAsync(wavBytes, null, cts.Token);
        });

        Assert.False(_service.IsPlaying);
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}
