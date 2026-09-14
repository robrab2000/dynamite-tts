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

    // Peak level (0..1) of what is actually being handed to the output device, with a short release
    // so a UI meter can poll it at frame rate. Written from the audio thread only.
    private volatile float _outputLevel;

    /// <summary>Current output peak level (0..1), smoothed; suitable for polling from the UI at ~60 fps.</summary>
    public float OutputLevel => _outputLevel;

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
            _player.Init(new MeteringSampleProvider(_mixer, this));
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

    public Task PlayAsync(byte[] audioData, string? targetDeviceId, CancellationToken cancellationToken)
    {
        if (audioData == null || audioData.Length == 0)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(targetDeviceId);

        WaveStream reader;
        try
        {
            reader = CreateWaveStream(new MemoryStream(audioData));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to decode the received audio payload.", ex);
        }

        ISampleProvider inputSampleProvider = reader.ToSampleProvider();
        return PlaySampleProviderAsync(inputSampleProvider, targetDeviceId, () => reader.Dispose(), cancellationToken);
    }

    /// <summary>
    /// Plays raw mono IEEE-float PCM (e.g. Kokoro 24 kHz) through the warm mixer.
    /// </summary>
    public Task PlayRawMonoFloatAsync(
        float[] samples,
        int sampleRate,
        string? targetDeviceId,
        CancellationToken cancellationToken)
    {
        if (samples == null || samples.Length == 0)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(targetDeviceId);

        var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        ISampleProvider provider = new MemorySampleProvider(samples, sourceFormat);

        if (provider.WaveFormat.SampleRate != _mixerFormat.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, _mixerFormat.SampleRate);
        }

        if (provider.WaveFormat.Channels == 1 && _mixerFormat.Channels == 2)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        return PlaySampleProviderAsync(provider, targetDeviceId, onCompletedExtra: null, cancellationToken);
    }

    /// <summary>
    /// Opens a gapless streaming input on the warm mixer. Segments written with
    /// <see cref="StreamingPlaybackSession.WriteAsync"/> start playing as soon as they are queued and
    /// play back-to-back while later segments are still being synthesized; writes apply
    /// back-pressure once <paramref name="maxBufferedSeconds"/> of audio is queued so synthesis
    /// runs only a little ahead of playback.
    /// </summary>
    public StreamingPlaybackSession BeginStream(
        int sampleRate,
        string? targetDeviceId,
        CancellationToken cancellationToken,
        double maxBufferedSeconds = 8.0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(targetDeviceId);

        var session = new StreamingPlaybackSession(this, sampleRate, maxBufferedSeconds, cancellationToken);
        lock (_lock)
        {
            if (_mixer == null)
                throw new InvalidOperationException("Audio output is not available.");
            _currentActiveCount++;
            _sessions.Add(session);
            _mixer.AddMixerInput(session.Output);
        }
        return session;
    }

    private readonly HashSet<StreamingPlaybackSession> _sessions = new();

    private void OnSessionFinished(StreamingPlaybackSession session)
    {
        lock (_lock)
        {
            if (_sessions.Remove(session))
            {
                _mixer?.RemoveMixerInput(session.Output);
                _currentActiveCount = Math.Max(0, _currentActiveCount - 1);
            }
        }
    }

    public sealed class StreamingPlaybackSession : IDisposable
    {
        private readonly AudioPlaybackService _owner;
        private readonly QueueSampleProvider _queue;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private readonly int _maxBufferedSamples;
        private readonly int _sampleRate;
        private int _finished;

        internal ISampleProvider Output { get; }

        /// <summary>
        /// Raised once, on a thread-pool thread, when the output device first pulls real (non-filler)
        /// audio from this stream, i.e. when the listener starts hearing it.
        /// </summary>
        public event Action? PlaybackStarted;

        /// <summary>Samples of silence inserted after playback started because synthesis fell behind.</summary>
        public long UnderrunSamples => Interlocked.Read(ref _queue.UnderrunSamples);

        /// <summary>Samples already pulled by the output device.</summary>
        public long PlayedSamples => _queue.PlayedSamples;

        public double PlayedSeconds => PlayedSamples / (double)_sampleRate;

        internal StreamingPlaybackSession(AudioPlaybackService owner, int sampleRate, double maxBufferedSeconds, CancellationToken ct)
        {
            _owner = owner;
            _sampleRate = sampleRate;
            _maxBufferedSamples = (int)(maxBufferedSeconds * sampleRate);
            _queue = new QueueSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1), () => PlaybackStarted?.Invoke());

            ISampleProvider provider = _queue;
            if (sampleRate != owner._mixerFormat.SampleRate)
                provider = new WdlResamplingSampleProvider(provider, owner._mixerFormat.SampleRate);
            if (owner._mixerFormat.Channels == 2)
                provider = new MonoToStereoSampleProvider(provider);

            Output = new CompletionTrackingSampleProvider(provider, () => Finish(canceled: false));
            _registration = ct.Register(() => Finish(canceled: true, ct));
        }

        /// <summary>Queues samples for playback, waiting while more than the allowed look-ahead is buffered.</summary>
        public async Task WriteAsync(float[] samples, CancellationToken ct)
        {
            if (samples == null || samples.Length == 0) return;
            if (_completion.Task.IsCompleted) return;

            _queue.Enqueue(samples);
            while (_queue.QueuedSamples > _maxBufferedSamples && !_completion.Task.IsCompleted)
            {
                await _queue.WaitForDrainAsync(ct);
            }
        }

        /// <summary>Marks the end of the stream and waits for the queued audio to finish playing.</summary>
        public Task CompleteAsync()
        {
            _queue.Complete();
            return _completion.Task;
        }

        /// <summary>Stops playback immediately and discards queued audio.</summary>
        public void Abort() => Finish(canceled: true);

        private void Finish(bool canceled, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _queue.Complete();
            _owner.OnSessionFinished(this);
            if (canceled)
                _completion.TrySetCanceled(ct);
            else
                _completion.TrySetResult();
        }

        public void Dispose()
        {
            _registration.Dispose();
            Finish(canceled: true);
        }
    }

    /// <summary>
    /// Pull-based provider fed by a queue of sample arrays. Underruns are filled with silence
    /// (so the stream stays open) until <see cref="Complete"/> is called, after which it drains
    /// and then reports end-of-stream.
    /// </summary>
    private sealed class QueueSampleProvider : ISampleProvider
    {
        private readonly object _sync = new();
        private readonly Queue<float[]> _chunks = new();
        private float[]? _current;
        private int _currentPos;
        private long _queued;
        private long _played;
        private bool _completed;
        private TaskCompletionSource? _drainSignal;
        private Action? _onFirstAudio;

        public WaveFormat WaveFormat { get; }
        public long QueuedSamples => Interlocked.Read(ref _queued);
        public long PlayedSamples => Interlocked.Read(ref _played);
        public long UnderrunSamples;

        public QueueSampleProvider(WaveFormat format, Action? onFirstAudio)
        {
            WaveFormat = format;
            _onFirstAudio = onFirstAudio;
        }

        public void Enqueue(float[] samples)
        {
            lock (_sync)
            {
                if (_completed) return;
                _chunks.Enqueue(samples);
                Interlocked.Add(ref _queued, samples.Length);
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _completed = true;
                _drainSignal?.TrySetResult();
            }
        }

        public Task WaitForDrainAsync(CancellationToken ct)
        {
            TaskCompletionSource tcs;
            lock (_sync)
            {
                _drainSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _drainSignal;
            }
            return tcs.Task.WaitAsync(ct);
        }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            int written = 0;
            int result;
            Action? firstAudio = null;

            lock (_sync)
            {
                while (written < buffer.Length)
                {
                    if (_current == null || _currentPos >= _current.Length)
                    {
                        if (_chunks.Count == 0) break;
                        _current = _chunks.Dequeue();
                        _currentPos = 0;
                    }

                    var n = Math.Min(buffer.Length - written, _current.Length - _currentPos);
                    new ReadOnlySpan<float>(_current, _currentPos, n).CopyTo(buffer.Slice(written));
                    _currentPos += n;
                    written += n;
                }

                if (written > 0)
                {
                    Interlocked.Add(ref _queued, -written);
                    Interlocked.Add(ref _played, written);
                    _drainSignal?.TrySetResult();
                    _drainSignal = null;

                    firstAudio = _onFirstAudio;
                    _onFirstAudio = null;
                }

                if (written == buffer.Length || _completed)
                {
                    result = written; // drained after Complete: report what we have (0 => end of stream)
                }
                else
                {
                    // Underrun: keep the stream alive with silence until more audio arrives. Only count it
                    // once playback has started; the wait for the first segment is not an underrun.
                    buffer.Slice(written).Clear();
                    if (Interlocked.Read(ref _played) > 0)
                        Interlocked.Add(ref UnderrunSamples, buffer.Length - written);
                    result = buffer.Length;
                }
            }

            // Never run listener code on the audio render thread.
            if (firstAudio != null)
                ThreadPool.UnsafeQueueUserWorkItem(static cb => cb(), firstAudio, preferLocal: false);

            return result;
        }
    }

    /// <summary>Pass-through on the final mix that records a smoothed peak level for UI meters.</summary>
    private sealed class MeteringSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioPlaybackService _owner;

        public MeteringSampleProvider(ISampleProvider source, AudioPlaybackService owner)
        {
            _source = source;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            var n = _source.Read(buffer);

            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                var a = Math.Abs(buffer[i]);
                if (a > peak) peak = a;
            }

            // Instant attack, ~150 ms release at typical 10 ms render blocks.
            var previous = _owner._outputLevel;
            _owner._outputLevel = peak >= previous ? peak : previous * 0.93f + peak * 0.07f;
            return n;
        }
    }

    private async Task PlaySampleProviderAsync(
        ISampleProvider inputSampleProvider,
        string? targetDeviceId,
        Action? onCompletedExtra,
        CancellationToken cancellationToken)
    {
        EnsureInitialized(targetDeviceId);

        if (inputSampleProvider.WaveFormat.SampleRate != _mixerFormat.SampleRate)
        {
            inputSampleProvider = new WdlResamplingSampleProvider(inputSampleProvider, _mixerFormat.SampleRate);
        }

        if (inputSampleProvider.WaveFormat.Channels == 1 && _mixerFormat.Channels == 2)
        {
            inputSampleProvider = new MonoToStereoSampleProvider(inputSampleProvider);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingProvider = new CompletionTrackingSampleProvider(inputSampleProvider, () =>
        {
            lock (_lock)
            {
                _currentActiveCount = Math.Max(0, _currentActiveCount - 1);
            }
            onCompletedExtra?.Invoke();
            tcs.TrySetResult();
        });

        lock (_lock)
        {
            if (cancellationToken.IsCancellationRequested || _mixer == null)
            {
                onCompletedExtra?.Invoke();
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
            onCompletedExtra?.Invoke();
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
        StreamingPlaybackSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.ToArray();
            _mixer?.RemoveAllMixerInputs();
            _currentActiveCount = 0;
        }
        foreach (var s in sessions) s.Abort();
    }

    private void Teardown()
    {
        foreach (var s in _sessions.ToArray()) s.Abort();

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
        _outputLevel = 0;
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

    /// <summary>
    /// Signals when the mixer has finished with an input. NAudio's MixingSampleProvider removes an
    /// input the moment it returns fewer samples than requested and never reads it again, so a short
    /// read is the end of the stream; waiting for a zero-length read would never complete (the
    /// request would stay "Speaking" forever). The callback runs on a thread-pool thread: it takes
    /// locks that Stop() holds while it touches the mixer, so running it on the render thread
    /// (inside the mixer's lock) could deadlock.
    /// </summary>
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

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            if (_completed) return 0;

            var read = _source.Read(buffer);
            if (read < buffer.Length)
            {
                _completed = true;
                ThreadPool.UnsafeQueueUserWorkItem(static cb => cb(), _onCompleted, preferLocal: false);
            }
            return read;
        }
    }
}
