using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DynamiteTts.Services;

public class AudioPlaybackService : IDisposable
{
    private readonly object _lock = new();
    private IWavePlayer? _player;
    private MixingSampleProvider? _mixer;
    private string? _currentDeviceId;
    private bool _isDisposed;

    private readonly WaveFormat _mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _currentActiveCount > 0;
            }
        }
    }

    private int _currentActiveCount;

    public AudioPlaybackService()
    {
    }

    public void WarmUp(string? targetDeviceId)
    {
        EnsureInitialized(targetDeviceId);
    }

    private void EnsureInitialized(string? targetDeviceId)
    {
        lock (_lock)
        {
            if (_player != null && string.Equals(_currentDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Teardown();

            _mixer = new MixingSampleProvider(_mixerFormat)
            {
                ReadFully = true
            };

            _player = CreatePlayer(targetDeviceId);
            _player.Init(_mixer);
            _player.Play();
            _currentDeviceId = targetDeviceId;
        }
    }

    public void PlayStopChime()
    {
        lock (_lock)
        {
            if (_mixer == null) return;

            var sampleRate = _mixerFormat.SampleRate;
            var durationSamples = (int)(sampleRate * 0.09); // 90ms
            var samples = new float[durationSamples * 2]; // stereo

            for (int i = 0; i < durationSamples; i++)
            {
                double progress = (double)i / durationSamples;
                double t = (double)i / sampleRate;
                // Soft frequency drop from 580Hz down to 420Hz with smooth exponential decay envelope
                double freq = 580.0 - (160.0 * progress);
                double envelope = Math.Pow(1.0 - progress, 2.5) * 0.12; // gentle 12% volume
                float sample = (float)(Math.Sin(2 * Math.PI * freq * t) * envelope);

                samples[i * 2] = sample;     // Left
                samples[i * 2 + 1] = sample; // Right
            }

            var chimeProvider = new MemorySampleProvider(samples, _mixerFormat);
            _mixer.AddMixerInput(chimeProvider);
        }
    }

    public async Task PlayAsync(byte[] audioData, string? targetDeviceId, CancellationToken cancellationToken)
    {
        if (audioData == null || audioData.Length == 0) return;

        cancellationToken.ThrowIfCancellationRequested();

        // Ensure output engine is warm & ready
        EnsureInitialized(targetDeviceId);

        using var ms = new MemoryStream(audioData);
        WaveStream reader;

        try
        {
            reader = CreateWaveStream(ms);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to decode the received audio payload.", ex);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ISampleProvider inputSampleProvider = reader.ToSampleProvider();

        // Resample and match channels to mixer format if needed
        if (inputSampleProvider.WaveFormat.SampleRate != _mixerFormat.SampleRate)
        {
            inputSampleProvider = new WdlResamplingSampleProvider(inputSampleProvider, _mixerFormat.SampleRate);
        }

        if (inputSampleProvider.WaveFormat.Channels == 1 && _mixerFormat.Channels == 2)
        {
            inputSampleProvider = new MonoToStereoSampleProvider(inputSampleProvider);
        }

        var trackingProvider = new CompletionTrackingSampleProvider(inputSampleProvider, () =>
        {
            lock (_lock)
            {
                _currentActiveCount = Math.Max(0, _currentActiveCount - 1);
            }
            reader.Dispose();
            tcs.TrySetResult();
        });

        lock (_lock)
        {
            if (cancellationToken.IsCancellationRequested || _mixer == null)
            {
                reader.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _currentActiveCount++;
            _mixer?.AddMixerInput(trackingProvider);
        }

        using (cancellationToken.Register(() =>
        {
            lock (_lock)
            {
                _mixer?.RemoveMixerInput(trackingProvider);
                _currentActiveCount = Math.Max(0, _currentActiveCount - 1);
            }
            reader.Dispose();
            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            await tcs.Task;
        }
    }

    private static WaveStream CreateWaveStream(MemoryStream ms)
    {
        var buffer = ms.ToArray();
        if (buffer.Length >= 4 &&
            buffer[0] == 'R' && buffer[1] == 'I' && buffer[2] == 'F' && buffer[3] == 'F')
        {
            return new WaveFileReader(new MemoryStream(buffer));
        }

        try
        {
            return new Mp3FileReader(new MemoryStream(buffer));
        }
        catch
        {
            return new StreamMediaFoundationReader(new MemoryStream(buffer));
        }
    }

    private static IWavePlayer CreatePlayer(string? targetDeviceId)
    {
#pragma warning disable CS0618
        MMDevice? selectedDevice = null;
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(targetDeviceId))
        {
            try
            {
                selectedDevice = enumerator.GetDevice(targetDeviceId);
            }
            catch
            {
                selectedDevice = null;
            }
        }

        if (selectedDevice == null || selectedDevice.State != DeviceState.Active)
        {
            try
            {
                selectedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                return new WaveOut();
            }
        }

        try
        {
            return new WasapiOut(selectedDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        }
        catch
        {
            return new WaveOut();
        }
#pragma warning restore CS0618
    }

    public void Stop()
    {
        lock (_lock)
        {
            _mixer?.RemoveAllMixerInputs();
            _currentActiveCount = 0;
        }
    }

    private void Teardown()
    {
        try
        {
            _player?.Stop();
        }
        catch { }

        try
        {
            _player?.Dispose();
        }
        catch { }
        _player = null;

        _mixer?.RemoveAllMixerInputs();
        _mixer = null;
        _currentActiveCount = 0;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        lock (_lock)
        {
            Teardown();
        }
    }

    private class MemorySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly WaveFormat _format;
        private int _position;

        public WaveFormat WaveFormat => _format;

        public MemorySampleProvider(float[] samples, WaveFormat format)
        {
            _samples = samples;
            _format = format;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_position >= _samples.Length)
                return 0;

            int samplesToCopy = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, samplesToCopy);
            _position += samplesToCopy;
            return samplesToCopy;
        }

        public int Read(Span<float> buffer)
        {
            if (_position >= _samples.Length)
                return 0;

            int count = Math.Min(buffer.Length, _samples.Length - _position);
            new ReadOnlySpan<float>(_samples, _position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private class CompletionTrackingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Action _onCompleted;
        private bool _completed;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public CompletionTrackingSampleProvider(ISampleProvider source, Action onCompleted)
        {
            _source = source;
            _onCompleted = onCompleted;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_completed) return 0;

            var read = _source.Read(buffer.AsSpan(offset, count));
            if (read == 0 && !_completed)
            {
                _completed = true;
                _onCompleted();
            }
            return read;
        }

        public int Read(Span<float> buffer)
        {
            if (_completed) return 0;

            var read = _source.Read(buffer);
            if (read == 0 && !_completed)
            {
                _completed = true;
                _onCompleted();
            }
            return read;
        }
    }
}
