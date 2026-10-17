using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DynamiteTts.Services;

/// <summary>
/// Fetches the CUDA 13 runtime DLLs that ONNX Runtime's CUDA provider links against
/// (cudart, cuBLAS, cuDNN, cuFFT, NVRTC) without a system-wide CUDA install.
///
/// NVIDIA publishes the exact same DLLs as Python wheels on PyPI; a wheel is a zip file, so the
/// DLLs are extracted straight into <c>%LocalAppData%\DynamiteTts\cuda13\bin</c> and that folder is
/// put on the DLL search path before the CUDA provider is loaded. Only the NVIDIA display driver
/// (which ships <c>nvcuda.dll</c>) has to be present on the machine.
/// </summary>
public sealed class CudaRuntimeService
{
    /// <summary>A pinned wheel: bump <see cref="ManifestVersion"/> whenever this list changes.</summary>
    public sealed record Package(string Name, string Version, string FileName, string Url, string Sha256, long Size, string[] Dlls);

    public const string ManifestVersion = "cuda13-ort1.29-v1";

    // Versions/URLs/hashes come from https://pypi.org/pypi/<name>/<version>/json (win_amd64 entries).
    // ORT 1.29's CUDA provider imports cudart64_13, cublas64_13, cublasLt64_13, cudnn64_9, cufft64_12
    // and nvrtc64_130_0 directly; cudnn64_9 loads its sub-libraries (adv/cnn/ops/graph/engines) lazily.
    public static readonly IReadOnlyList<Package> Packages = new[]
    {
        new Package("nvidia-cuda-runtime", "13.3.29", "nvidia_cuda_runtime-13.3.29-py3-none-win_amd64.whl",
            "https://files.pythonhosted.org/packages/d2/27/b53a5e0397842a5c11f0e1a39d4e5b2f22638a4126e83b3c4e196f62c969/nvidia_cuda_runtime-13.3.29-py3-none-win_amd64.whl",
            "0667ec61c3d897388efa305ed4f7609ace88849a753ba9c6311d06dca55fff4f", 2_630_354,
            new[] { "cudart64_13.dll" }),
        new Package("nvidia-cuda-nvrtc", "13.3.33", "nvidia_cuda_nvrtc-13.3.33-py3-none-win_amd64.whl",
            "https://files.pythonhosted.org/packages/a1/42/edce72f2c5a0f587168109c867f25f4a9a6cd7289ecf0d68ed2b1070f273/nvidia_cuda_nvrtc-13.3.33-py3-none-win_amd64.whl",
            "7d2af818851c0c224d5f92221e9226e51ee23c236df4b51f9194563979c888be", 45_319_163,
            new[] { "nvrtc64_130_0.dll", "nvrtc-builtins64_133.dll" }),
        new Package("nvidia-cufft", "12.3.0.29", "nvidia_cufft-12.3.0.29-py3-none-win_amd64.whl",
            "https://files.pythonhosted.org/packages/94/64/8e9d808720559d3cbfcd1d1bc8a2e6f55deb29d692513d5a93c8d417b7e5/nvidia_cufft-12.3.0.29-py3-none-win_amd64.whl",
            "510036a2bbab5c83ae93dc5c907c3a49d3518e3066ac3a2052ff0f7f9b27dfc4", 183_939_745,
            new[] { "cufft64_12.dll" }),
        new Package("nvidia-cublas", "13.6.0.2", "nvidia_cublas-13.6.0.2-py3-none-win_amd64.whl",
            "https://files.pythonhosted.org/packages/08/8f/890a96ea1ff615100296977cce23296052dcb8c114d4e451201ec39df9bf/nvidia_cublas-13.6.0.2-py3-none-win_amd64.whl",
            "3b5bcd6bfb6f65010ebf195851bcb9b2aa34b9fe08479432002991c1fe84b67d", 394_568_225,
            new[] { "cublas64_13.dll", "cublasLt64_13.dll" }),
        new Package("nvidia-cudnn-cu13", "9.25.1.1", "nvidia_cudnn_cu13-9.25.1.1-py3-none-win_amd64.whl",
            "https://files.pythonhosted.org/packages/fd/0f/d7e4141c1126899c7b8d202eb3085380164beefef32f94cc8967ed3a00ff/nvidia_cudnn_cu13-9.25.1.1-py3-none-win_amd64.whl",
            "e1de75bf1ad9040414f9b13cc87135d660d13dd3c859180dd6b63353e571f860", 407_762_053,
            new[]
            {
                "cudnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_cnn64_9.dll", "cudnn_engines_precompiled64_9.dll",
                "cudnn_engines_runtime_compiled64_9.dll", "cudnn_engines_tensor_ir64_9.dll", "cudnn_ext64_9.dll",
                "cudnn_graph64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll"
            })
    };

    public static long TotalDownloadBytes => Packages.Sum(p => p.Size);

    public static string RuntimeDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamiteTts", "cuda13");

    public static string BinDirectory => Path.Combine(RuntimeDirectory, "bin");
    private static string DownloadDirectory => Path.Combine(RuntimeDirectory, "downloads");
    private static string MarkerPath => Path.Combine(RuntimeDirectory, "installed.txt");

    private readonly object _lock = new();
    private Task? _installTask;
    private static int _dllDirectoryRegistered;

    /// <summary>Fraction of the total download completed (0..1) while an install is running.</summary>
    public double Progress { get; private set; }

    /// <summary>Raised on a background thread with the overall progress fraction.</summary>
    public event Action<double>? ProgressChanged;

    public bool IsInstalling { get { lock (_lock) return _installTask != null; } }

    /// <summary>True when every DLL the CUDA provider needs is present and matches the pinned manifest.</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(MarkerPath) || File.ReadAllText(MarkerPath).Trim() != ManifestVersion)
                return false;
            return Packages.All(PackageIsExtracted);
        }
        catch
        {
            return false;
        }
    }

    private static bool PackageIsExtracted(Package p) =>
        p.Dlls.All(d => File.Exists(Path.Combine(BinDirectory, d)) && new FileInfo(Path.Combine(BinDirectory, d)).Length > 0);

    /// <summary>
    /// Downloads and extracts any packages that are missing. Safe to call repeatedly: a second call
    /// while an install is running joins the in-flight task.
    /// </summary>
    public Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled()) return Task.CompletedTask;

        lock (_lock)
        {
            if (_installTask != null) return _installTask;
            _installTask = Task.Run(() => InstallCoreAsync(cancellationToken), cancellationToken);
            return _installTask.ContinueWith(t =>
            {
                lock (_lock) _installTask = null;
                if (t.IsFaulted) throw t.Exception!.GetBaseException();
                if (t.IsCanceled) throw new OperationCanceledException(cancellationToken);
            }, TaskScheduler.Default);
        }
    }

    private async Task InstallCoreAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(BinDirectory);
        Directory.CreateDirectory(DownloadDirectory);

        // Stale marker from an older manifest: drop it so a half-updated folder is never reported as ready.
        try { File.Delete(MarkerPath); } catch { }

        long total = TotalDownloadBytes;
        long done = Packages.Where(PackageIsExtracted).Sum(p => p.Size);
        Report(done, total);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0 (+https://github.com/)");

        foreach (var package in Packages)
        {
            ct.ThrowIfCancellationRequested();
            if (PackageIsExtracted(package)) continue;

            var wheelPath = Path.Combine(DownloadDirectory, package.FileName);
            if (!File.Exists(wheelPath) || !await HashMatchesAsync(wheelPath, package.Sha256, ct))
            {
                var packageStart = done;
                await DownloadAsync(http, package, wheelPath, bytes => Report(packageStart + bytes, total), ct);
                if (!await HashMatchesAsync(wheelPath, package.Sha256, ct))
                {
                    try { File.Delete(wheelPath); } catch { }
                    throw new InvalidDataException($"Checksum mismatch for {package.FileName}.");
                }
            }

            Extract(wheelPath, package);
            try { File.Delete(wheelPath); } catch { }

            done += package.Size;
            Report(done, total);
        }

        File.WriteAllText(MarkerPath, ManifestVersion);
        Report(total, total);
    }

    private static async Task DownloadAsync(HttpClient http, Package package, string target, Action<long> onProgress, CancellationToken ct)
    {
        var tmp = target + ".tmp";
        using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true))
        {
            var buffer = new byte[1 << 17];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                onProgress(Math.Min(received, package.Size));
            }
        }

        File.Move(tmp, target, overwrite: true);
    }

    private static async Task<bool> HashMatchesAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void Extract(string wheelPath, Package package)
    {
        using var zip = ZipFile.OpenRead(wheelPath);
        var wanted = new HashSet<string>(package.Dlls, StringComparer.OrdinalIgnoreCase);
        var found = 0;

        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (!wanted.Contains(name)) continue;

            var target = Path.Combine(BinDirectory, name);
            var tmp = target + ".tmp";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            found++;
        }

        if (found != wanted.Count)
            throw new InvalidDataException($"{package.FileName} did not contain all expected DLLs ({found}/{wanted.Count}).");
    }

    private void Report(long done, long total)
    {
        Progress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        ProgressChanged?.Invoke(Progress);
    }

    // ------------------------------------------------------------------ DLL search path

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string path);

    /// <summary>
    /// Makes the extracted DLLs resolvable for <c>onnxruntime_providers_cuda.dll</c>'s static imports.
    /// The provider is loaded with the default search order, which consults the process PATH, so the
    /// bin folder is prepended there; AddDllDirectory covers loaders that use the safe-search flags.
    /// </summary>
    public static void RegisterDllDirectory()
    {
        if (Interlocked.Exchange(ref _dllDirectoryRegistered, 1) == 1) return;

        var bin = BinDirectory;
        if (!Directory.Exists(bin)) return;

        try { AddDllDirectory(bin); } catch { }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Split(';').Any(p => string.Equals(p.Trim().TrimEnd('\\'), bin, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", bin + ";" + path);
    }

    /// <summary>Removes the extracted runtime (about 1.4 GB).</summary>
    public static void Uninstall()
    {
        try { if (Directory.Exists(RuntimeDirectory)) Directory.Delete(RuntimeDirectory, recursive: true); } catch { }
    }
}
