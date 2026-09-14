using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KokoroBenchCuda129;

internal static class Program
{
    private const string SampleText =
        "The quick brown fox jumps over the lazy dog. This is a benchmark of local text to speech synthesis. " +
        "We want to know how long it takes before the first audio segment is ready, and how long the whole " +
        "paragraph takes to render. A typical highlighted paragraph from a web page is roughly this long, " +
        "with a handful of sentences of varying length, some short, and some that run on for a little while " +
        "before finally coming to an end. That should be enough text to make the numbers meaningful. " +
        "And here is a little more text so that we can reach the model's maximum of five hundred and ten tokens, " +
        "which lets us measure the cost of the largest possible segment as well as the smallest ones.";

    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "all";
        if (mode == "api") { DumpApi(); return 0; }
        if (mode == "probe") { Probe(args); return 0; }

        Console.WriteLine($"ORT providers: {string.Join(", ", OrtEnv.Instance().GetAvailableProviders())}");
        Console.WriteLine($"Logical CPUs: {Environment.ProcessorCount}");
        Console.WriteLine();

        Console.WriteLine("DXGI adapters:");
        foreach (var (idx, name) in EnumerateAdapters())
            Console.WriteLine($"  [{idx}] {name}");
        Console.WriteLine();

        var configs = new List<(string name, Func<SessionOptions?> opts, KModel model)>();
        void Add(string name, Func<SessionOptions?> opts, KModel model) => configs.Add((name, opts, model));

        var wantCpu = mode is "all" or "cpu";
        var wantDml = mode is "all" or "dml";

        if (wantCpu)
        {
            Add("CPU default (KokoroSharp opts) fp32", () => null, KModel.float32);
            Add("CPU default (KokoroSharp opts) fp16", () => null, KModel.float16);
            Add("CPU tuned (intra=12, seq, all-opt) fp32", () => new SessionOptions
            {
                IntraOpNumThreads = 12,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            }, KModel.float32);
        }

        if (wantDml)
        {
            foreach (var (idx, name) in EnumerateAdapters())
            {
                if (name.Contains("Meta Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                var id = idx;
                Add($"DML[{idx}] {name} fp16", () => Dml(id), KModel.float16);
                Add($"DML[{idx}] {name} fp32", () => Dml(id), KModel.float32);
            }
        }

        if (args.Length > 1)
        {
            var filter = args[1];
            configs = configs.Where(c => c.name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var results = new List<string>();
        foreach (var (name, opts, model) in configs)
        {
            Console.WriteLine($"=== {name}");
            try
            {
                var r = await RunOne(opts(), model);
                results.Add($"| {name} | {r.loadMs,6:F0} | {r.firstRunFirstMs,6:F0} | {r.firstRunTotalMs,6:F0} | {r.secondRunFirstMs,6:F0} | {r.secondRunTotalMs,6:F0} | {r.audioSec,5:F1} | {r.rtf,5:F1}x |");
            }
            catch (Exception ex)
            {
                var first = ex.Message.Split('\n')[0];
                Console.WriteLine($"  FAILED: {ex.GetType().Name}: {first}");
                results.Add($"| {name} | FAIL: {first[..Math.Min(80, first.Length)]} |");
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Console.WriteLine();
        Console.WriteLine("| Backend | Load ms | Run1 first-audio ms | Run1 total ms | Run2 first-audio ms | Run2 total ms | Audio s | Run2 realtime factor |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results) Console.WriteLine(r);
        return 0;
    }

    // ------------------------------------------------------------------
    // probe <modelPath> <cpu|dml:N> [intra] [inter] [lengths]
    // Runs the ONNX graph directly (no KokoroSharp engine) so we can measure
    // per-segment-length cost, thread settings, alternative models, and catch
    // DirectML failures on our own thread.
    // ------------------------------------------------------------------
    private static void Probe(string[] args)
    {
        var modelPath = args[1];
        var backend = args.Length > 2 ? args[2] : "cpu";
        var intra = args.Length > 3 ? int.Parse(args[3]) : 8;
        var inter = args.Length > 4 ? int.Parse(args[4]) : 1;
        var lengths = args.Length > 5
            ? args[5].Split(',').Select(int.Parse).ToArray()
            : new[] { 16, 32, 48, 64, 96, 128, 192, 256, 384, 510 };

        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (backend.StartsWith("dml"))
        {
            var id = backend.Contains(':') ? int.Parse(backend.Split(':')[1]) : 0;
            opts = Dml(id);
        }
        else if (backend.StartsWith("cuda"))
        {
            var parts = backend.Split(':');
            var id = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            var algo = parts.Length > 2 ? parts[2] : "exhaustive";
            var cudaOpts = new OrtCUDAProviderOptions();
            cudaOpts.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = id.ToString(),
                ["cudnn_conv_algo_search"] = algo.ToUpperInvariant(),   // EXHAUSTIVE | HEURISTIC | DEFAULT
                ["cudnn_conv_use_max_workspace"] = "1",
                ["arena_extend_strategy"] = "kSameAsRequested"
            });
            opts.AppendExecutionProvider_CUDA(cudaOpts);
        }
        else
        {
            opts.IntraOpNumThreads = intra;
            opts.InterOpNumThreads = inter;
            opts.ExecutionMode = inter > 1 ? ExecutionMode.ORT_PARALLEL : ExecutionMode.ORT_SEQUENTIAL;
        }

        Console.WriteLine($"probe model={Path.GetFileName(modelPath)} ({new FileInfo(modelPath).Length / 1048576} MB) backend={backend} intra={intra} inter={inter}");
        var sw = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath, opts);
        Console.WriteLine($"  session created in {sw.ElapsedMilliseconds} ms; inputs: {string.Join(", ", session.InputMetadata.Select(kv => $"{kv.Key}:{kv.Value.ElementType}{string.Join("x", kv.Value.Dimensions)}"))}; outputs: {string.Join(", ", session.OutputMetadata.Keys)}");

        var tokenName = session.InputMetadata.Keys.FirstOrDefault(k => k is "tokens" or "input_ids") ?? session.InputMetadata.Keys.First();
        var styleName = session.InputMetadata.Keys.First(k => k == "style");
        var speedName = session.InputMetadata.Keys.First(k => k == "speed");

        var voice = KokoroVoiceManager.GetVoice("am_adam");
        float[,,] features = voice.Features;
        var lang = KokoroLangCodeHelper.GetLangCode(voice);
        sw.Restart();
        var tokens = Tokenizer.Tokenize(SampleText, lang, true);
        Console.WriteLine($"  tokenized {SampleText.Length} chars -> {tokens.Length} tokens in {sw.ElapsedMilliseconds} ms (first call includes dictionary load)");
        sw.Restart();
        tokens = Tokenizer.Tokenize(SampleText, lang, true);
        Console.WriteLine($"  tokenize again: {sw.ElapsedMilliseconds} ms");

        Console.WriteLine();
        Console.WriteLine("| tokens | run1 ms | run2 ms | run3 ms | audio s | realtime x (run3) | ms/token (run3) |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var L in lengths)
        {
            if (L > tokens.Length) break;
            var seg = tokens[..L];
            var C = features.GetLength(2);
            var tokenTensor = new DenseTensor<long>(new[] { 1, L + 2 });
            for (int i = 0; i < L; i++) tokenTensor[0, i + 1] = seg[i] >= 0 ? seg[i] : 4;
            var styleTensor = new DenseTensor<float>(new[] { 1, C });
            for (int j = 0; j < C; j++) styleTensor[0, j] = features[L - 1, 0, j];
            var speedTensor = new DenseTensor<float>(new[] { 1.28f }, new[] { 1 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(tokenName, tokenTensor),
                NamedOnnxValue.CreateFromTensor(styleName, styleTensor),
                NamedOnnxValue.CreateFromTensor(speedName, speedTensor)
            };

            var times = new double[3];
            int outLen = 0;
            try
            {
                for (int r = 0; r < 3; r++)
                {
                    sw.Restart();
                    using var results = session.Run(inputs);
                    times[r] = sw.Elapsed.TotalMilliseconds;
                    outLen = (int)results[0].AsTensor<float>().Length;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"| {L} | FAIL: {ex.Message.Split('\n')[0][..Math.Min(100, ex.Message.Split('\n')[0].Length)]} |");
                continue;
            }
            var audio = outLen / 24000.0;
            Console.WriteLine($"| {L} | {times[0]:F0} | {times[1]:F0} | {times[2]:F0} | {audio:F2} | {audio / (times[2] / 1000):F1}x | {times[2] / L:F1} |");
        }
    }

    private static SessionOptions Dml(int deviceId)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            options.AppendExecutionProvider("DML", new Dictionary<string, string>
            {
                ["device_filter"] = "gpu",
                ["device_id"] = deviceId.ToString()
            });
        }
        catch
        {
            options.AppendExecutionProvider_DML(deviceId);
        }
        return options;
    }

    private static async Task<(double loadMs, double firstRunFirstMs, double firstRunTotalMs, double secondRunFirstMs, double secondRunTotalMs, double audioSec, double rtf)>
        RunOne(SessionOptions? opts, KModel model)
    {
        var sw = Stopwatch.StartNew();
        using var synth = await KokoroWavSynthesizer.LoadModelAsync(model: model, sessionOptions: opts);
        var loadMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  load: {loadMs:F0} ms");

        var voice = KokoroVoiceManager.GetVoice("am_adam");
        var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig { MaxFirstSegmentLength = 48 })
        {
            Speed = 1.28f
        };

        var run1 = Synthesize(synth, voice, config, verbose: true);
        var run2 = Synthesize(synth, voice, config, verbose: true);
        var rtf = run2.audioSec / (run2.totalMs / 1000.0);
        Console.WriteLine($"  run1: first={run1.firstMs:F0} ms total={run1.totalMs:F0} ms | run2: first={run2.firstMs:F0} ms total={run2.totalMs:F0} ms | audio={run2.audioSec:F1}s | {rtf:F1}x realtime");
        return (loadMs, run1.firstMs, run1.totalMs, run2.firstMs, run2.totalMs, run2.audioSec, rtf);
    }

    private static (double firstMs, double totalMs, double audioSec) Synthesize(
        KokoroWavSynthesizer synth, KokoroVoice voice, KokoroTTSPipelineConfig config, bool verbose)
    {
        var sw = Stopwatch.StartNew();
        double firstMs = -1;
        long samples = 0;
        var segTimes = new List<(double ms, int len)>();
        var done = new ManualResetEventSlim(false);

        synth.Synthesize(
            SampleText,
            voice,
            OnProgress: s =>
            {
                var t = sw.Elapsed.TotalMilliseconds;
                if (firstMs < 0) firstMs = t;
                samples += s.Length;
                segTimes.Add((t, s.Length));
            },
            OnComplete: () => done.Set(),
            pipelineConfig: config);
        var returnedMs = sw.Elapsed.TotalMilliseconds;
        done.Wait(TimeSpan.FromMinutes(5));
        var totalMs = sw.Elapsed.TotalMilliseconds;

        if (verbose)
        {
            Console.WriteLine($"    Synthesize() returned after {returnedMs:F0} ms; OnComplete after {totalMs:F0} ms; segments={segTimes.Count}");
            foreach (var (ms, len) in segTimes)
                Console.WriteLine($"      seg @ {ms,7:F0} ms  {len / 24000.0,5:F2}s audio");
        }
        return (firstMs, totalMs, samples / 24000.0);
    }

    private static void DumpApi()
    {
        var asm = typeof(KokoroTTS).Assembly;
        foreach (var t in asm.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName))
        {
            Console.WriteLine($"\n### {t.FullName} : {t.BaseType?.Name}");
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
                Console.WriteLine($"  {m.MemberType}: {m}");
            }
        }
    }

    // --- DXGI adapter enumeration (raw vtable call; the ComImport cast fails in an MTA console host) ---
    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, out IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppFactory);

    private static unsafe List<(int idx, string name)> EnumerateAdapters()
    {
        var list = new List<(int, string)>();
        if (CreateDXGIFactory(new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), out var pFactory) != 0) return list;
        var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(pFactory);
        for (uint i = 0; ; i++)
        {
            if (factory.EnumAdapters(i, out var pAdapter) != 0 || pAdapter == IntPtr.Zero) break;
            // IDXGIAdapter::GetDesc is vtable slot 8 (IUnknown 0-2, IDXGIObject 3-6, EnumOutputs 7, GetDesc 8)
            var vtbl = *(IntPtr**)pAdapter;
            var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)vtbl[8];
            DXGI_ADAPTER_DESC desc;
            var hr = getDesc(pAdapter, &desc);
            list.Add(((int)i, hr == 0 ? new string(desc.Description).TrimEnd('\0').Trim() : $"(GetDesc failed 0x{hr:X8})"));
            Marshal.Release(pAdapter);
        }
        Marshal.Release(pFactory);
        return list;
    }
}
