using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace DynamiteTts.Services;

public class LocalTtsService : IDisposable
{
    private readonly object _lock = new();
    private KokoroWavSynthesizer? _synthesizer;
    private bool _isInitializing;
    private bool _isDisposed;
    private bool _useDirectMl = true;
    private int _deviceId = -1;
    private string _precision = "float16";
    private bool _isDirectMlActive;
    private string _activeDeviceDescription = "Not initialized";

    public bool IsInitialized
    {
        get
        {
            lock (_lock)
            {
                return _synthesizer != null;
            }
        }
    }

    public bool IsDirectMlActive
    {
        get
        {
            lock (_lock)
            {
                return _isDirectMlActive;
            }
        }
    }

    public int CurrentDeviceId
    {
        get
        {
            lock (_lock)
            {
                return _deviceId;
            }
        }
    }

    public string CurrentPrecision
    {
        get
        {
            lock (_lock)
            {
                return _precision;
            }
        }
    }

    public string ActiveDeviceDescription
    {
        get
        {
            lock (_lock)
            {
                return _activeDeviceDescription;
            }
        }
    }

    public async Task InitializeAsync(
        bool useDirectMl = true,
        int deviceId = -1,
        string precision = "float16",
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return;

        // Sentinel for the unavailable NPU list entry — map back to Auto GPU
        if (deviceId == -100)
            deviceId = -1;

        lock (_lock)
        {
            if (_synthesizer != null &&
                _useDirectMl == useDirectMl &&
                _deviceId == deviceId &&
                string.Equals(_precision, precision, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isInitializing) return;
            _isInitializing = true;
            _useDirectMl = useDirectMl;
            _deviceId = deviceId;
            _precision = precision;
        }

        try
        {
            await Task.Run(async () =>
            {
                SessionOptions? options = null;
                bool directMlSuccess = false;
                string deviceDesc = "CPU";

                if (useDirectMl)
                {
                    try
                    {
                        options = CreateDirectMlSessionOptions(deviceId, out deviceDesc);
                        directMlSuccess = options != null;
                    }
                    catch
                    {
                        options = null;
                        directMlSuccess = false;
                        deviceDesc = "CPU (DirectML init failed)";
                    }
                }

                var kModel = string.Equals(precision, "float32", StringComparison.OrdinalIgnoreCase)
                    ? KModel.float32
                    : KModel.float16;

                try
                {
                    var instance = await KokoroWavSynthesizer.LoadModelAsync(
                        model: kModel,
                        sessionOptions: options);

                    lock (_lock)
                    {
                        _synthesizer?.Dispose();
                        _synthesizer = instance;
                        _isDirectMlActive = directMlSuccess;
                        _activeDeviceDescription = directMlSuccess ? deviceDesc : "CPU";
                    }
                }
                catch when (useDirectMl)
                {
                    var cpuInstance = await KokoroWavSynthesizer.LoadModelAsync(
                        model: kModel,
                        sessionOptions: null);

                    lock (_lock)
                    {
                        _synthesizer?.Dispose();
                        _synthesizer = cpuInstance;
                        _isDirectMlActive = false;
                        _activeDeviceDescription = "CPU (model load fell back)";
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                _isInitializing = false;
            }
        }
    }

    private static SessionOptions? CreateDirectMlSessionOptions(int deviceId, out string deviceDescription)
    {
        int targetDevice = deviceId >= 0 ? deviceId : DirectMlDeviceService.ResolvePreferredGpuDeviceId();
        deviceDescription = $"DirectML GPU adapter {targetDevice}";

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        try
        {
            options.AppendExecutionProvider("DML", new Dictionary<string, string>
            {
                ["device_filter"] = "gpu",
                ["device_id"] = targetDevice.ToString()
            });
            return options;
        }
        catch
        {
            options.AppendExecutionProvider_DML(targetDevice);
            return options;
        }
    }

    public async Task GenerateSpeechStreamingAsync(
        string text,
        string voiceName,
        double speed,
        int deviceId,
        string precision,
        Func<float[], Task> onSegmentAsync,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (deviceId == -100)
            deviceId = -1;

        if (!IsInitialized || _deviceId != deviceId || !string.Equals(_precision, precision, StringComparison.OrdinalIgnoreCase))
        {
            await InitializeAsync(_useDirectMl, deviceId, precision, cancellationToken);
        }

        KokoroWavSynthesizer synth;
        lock (_lock)
        {
            if (_synthesizer == null)
                throw new InvalidOperationException("Local Kokoro TTS engine could not be initialized.");
            synth = _synthesizer;
        }

        var voice = ResolveVoice(voiceName);
        var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig
        {
            MaxFirstSegmentLength = 48
        })
        {
            Speed = (float)Math.Clamp(speed, 0.5, 2.0)
        };

        var channel = System.Threading.Channels.Channel.CreateUnbounded<float[]>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

        var playbackTask = Task.Run(async () =>
        {
            await foreach (var samples in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (samples.Length == 0) continue;
                await onSegmentAsync(samples);
            }
        }, cancellationToken);

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                synth.Synthesize(
                    text,
                    voice,
                    OnProgress: samples =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        if (samples == null || samples.Length == 0) return;
                        var copy = new float[samples.Length];
                        Array.Copy(samples, copy, samples.Length);
                        channel.Writer.TryWrite(copy);
                    },
                    OnComplete: () => channel.Writer.TryComplete(),
                    pipelineConfig: config);

                channel.Writer.TryComplete();
            }, cancellationToken);
        }
        catch
        {
            channel.Writer.TryComplete();
            throw;
        }

        await playbackTask;
    }

    public async Task<byte[]> GenerateSpeechWavAsync(
        string text,
        string voiceName,
        double speed,
        int deviceId = -1,
        string precision = "float16",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        if (deviceId == -100)
            deviceId = -1;

        if (!IsInitialized || _deviceId != deviceId || !string.Equals(_precision, precision, StringComparison.OrdinalIgnoreCase))
        {
            await InitializeAsync(_useDirectMl, deviceId, precision, cancellationToken);
        }

        KokoroWavSynthesizer synth;
        lock (_lock)
        {
            if (_synthesizer == null)
                throw new InvalidOperationException("Local Kokoro TTS engine could not be initialized.");
            synth = _synthesizer;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var voice = ResolveVoice(voiceName);
        var config = new KokoroTTSPipelineConfig
        {
            Speed = (float)Math.Clamp(speed, 0.5, 2.0)
        };

        var pcmBytes = await synth.SynthesizeAsync(text, voice, config);
        if (pcmBytes == null || pcmBytes.Length == 0)
            return Array.Empty<byte>();

        cancellationToken.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        var waveFormat = new WaveFormat(24000, 16, 1);
        using (var writer = new WaveFileWriter(ms, waveFormat))
        {
            writer.Write(pcmBytes, 0, pcmBytes.Length);
        }

        return ms.ToArray();
    }

    public static KokoroVoice ResolveVoice(string voiceName)
    {
        try
        {
            var clean = voiceName?.Trim() ?? "af_heart";
            if (string.Equals(clean, "coral", StringComparison.OrdinalIgnoreCase))
                clean = "af_heart";
            else if (string.Equals(clean, "alloy", StringComparison.OrdinalIgnoreCase))
                clean = "af_sarah";
            else if (string.Equals(clean, "echo", StringComparison.OrdinalIgnoreCase))
                clean = "am_echo";
            else if (string.Equals(clean, "fable", StringComparison.OrdinalIgnoreCase))
                clean = "bm_fable";
            else if (string.Equals(clean, "onyx", StringComparison.OrdinalIgnoreCase))
                clean = "am_fenrir";
            else if (string.Equals(clean, "nova", StringComparison.OrdinalIgnoreCase))
                clean = "af_sky";
            else if (string.Equals(clean, "shimmer", StringComparison.OrdinalIgnoreCase))
                clean = "af_bella";
            else if (string.Equals(clean, "ash", StringComparison.OrdinalIgnoreCase))
                clean = "am_adam";
            else if (string.Equals(clean, "sage", StringComparison.OrdinalIgnoreCase))
                clean = "af_nicole";

            var voice = KokoroVoiceManager.GetVoice(clean);
            if (voice != null) return voice;
        }
        catch { }

        return KokoroVoiceManager.GetVoice("af_heart") ?? KokoroVoiceManager.Voices[0];
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_lock)
        {
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
    }
}
