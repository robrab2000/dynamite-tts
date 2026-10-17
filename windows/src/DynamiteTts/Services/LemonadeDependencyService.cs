using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamiteTts.Services;

/// <summary>
/// Locates / installs Lemonade Server and ensures a small CPU-friendly chat model is available
/// for Summary mode (single-flight, same idea as the CUDA runtime download).
/// </summary>
public class LemonadeDependencyService
{
    /// <summary>Small registry model that runs on CPU; AMD NPU/GPU is optional acceleration.</summary>
    public const string DefaultChatModelId = "Qwen2.5-0.5B-Instruct";

    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ServeWarmup = TimeSpan.FromSeconds(8);

    private readonly LemonadeChatClient _chatClient;
    private readonly HttpClient _httpClient;
    private readonly object _ensureLock = new();
    private Task<string>? _ensureTask;

    public LemonadeDependencyService(LemonadeChatClient? chatClient = null, HttpClient? httpClient = null)
    {
        _chatClient = chatClient ?? new LemonadeChatClient();
        _httpClient = httpClient ?? new HttpClient { Timeout = PullTimeout };
        if (httpClient == null)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0");
    }

    public bool IsLemonadeCliAvailable() =>
        FindLemonadeServerExecutable() != null || FindLemonadeCliExecutable() != null;

    /// <summary>
    /// Prefer the current server binary (<c>LemonadeServer.exe</c>), then legacy
    /// <c>lemonade-server.exe</c>, then the CLI (<c>lemonade.exe</c>).
    /// </summary>
    public string? FindLemonadeExecutable() =>
        FindLemonadeServerExecutable() ?? FindLemonadeCliExecutable();

    /// <summary>HTTP server process for current Lemonade installs.</summary>
    public string? FindLemonadeServerExecutable()
    {
        var server = FindNamedExecutable("LemonadeServer.exe")
                     ?? FindNamedExecutable("lemonade-server.exe");
        if (server != null) return server;

        // Winget layout: lemonade.exe (CLI) and LemonadeServer.exe share a bin folder.
        var cli = FindNamedExecutable("lemonade.exe");
        if (cli != null)
        {
            var dir = Path.GetDirectoryName(cli);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var name in new[] { "LemonadeServer.exe", "lemonade-server.exe" })
                {
                    var sibling = Path.Combine(dir, name);
                    if (File.Exists(sibling)) return sibling;
                }
            }
        }

        return null;
    }

    /// <summary>CLI client used for <c>pull</c>/<c>load</c> (not the HTTP server).</summary>
    public string? FindLemonadeCliExecutable() => FindNamedExecutable("lemonade.exe");

    /// <summary>
    /// True when <paramref name="exePath"/> is the dedicated server binary (no <c>serve</c> subcommand).
    /// </summary>
    public static bool IsServerExecutable(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath);
        return name.Equals("LemonadeServer.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("lemonade-server.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arguments for launching Lemonade. Current <c>LemonadeServer.exe</c> uses <c>--silent</c>
    /// (same as the Windows Startup shortcut); older all-in-one CLIs used <c>serve --no-tray</c>.
    /// </summary>
    public static string BuildServerStartArguments(string exePath, int port)
    {
        if (IsServerExecutable(exePath))
            return "--silent";

        return $"serve --no-tray --port {port}";
    }

    private static string? FindNamedExecutable(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(p.Trim('"'), fileName);
            if (File.Exists(fullPath)) return fullPath;
        }

        foreach (var dir in GetLemonadeSearchDirectories())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal static IEnumerable<string> GetLemonadeSearchDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            Path.Combine(local, "lemonade_server", "bin"),
            Path.Combine(local, "Programs", "Lemonade"),
            Path.Combine(local, "Programs", "LemonadeServer"),
            Path.Combine(local, "Programs", "lemonade_server", "bin"),
            Path.Combine(programFiles, "Lemonade"),
            Path.Combine(programFiles, "LemonadeServer"),
            Path.Combine(programFiles, "AMD", "Lemonade"),
            Path.Combine(programFiles, "lemonade_server", "bin")
        ];
    }

    /// <summary>
    /// Starts the Lemonade HTTP API without forcing a TTS model.
    /// Current installs: launch <c>LemonadeServer.exe</c>. Legacy: <c>lemonade serve --no-tray</c>.
    /// </summary>
    public async Task<bool> TryStartLemonadeServerAsync(
        string? chatEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        var exe = FindLemonadeServerExecutable() ?? FindLemonadeCliExecutable();
        if (exe == null) return false;

        try
        {
            var port = TryGetPort(chatEndpoint) ?? 13305;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = BuildServerStartArguments(exe, port),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["LEMONADE_PORT"] = port.ToString();

            AppLog.Info($"Starting Lemonade via '{exe}' {psi.Arguments} (port {port})");
            var process = Process.Start(psi);
            if (process == null)
            {
                AppLog.Warn("Lemonade Process.Start returned null");
                return false;
            }

            await Task.Delay(ServeWarmup, cancellationToken);

            // Bad CLI args (e.g. legacy `serve`) exit immediately; do not report a successful start.
            if (process.HasExited)
            {
                AppLog.Warn($"Lemonade exited immediately with code {process.ExitCode} ({exe})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Failed to start Lemonade Server", ex);
            return false;
        }
    }

    public async Task<bool> InstallViaWingetAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Running winget install AMD.LemonadeServer...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install AMD.LemonadeServer --accept-package-agreements --accept-source-agreements --silent",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures Lemonade is reachable and a chat (non-TTS) model is available. Single-flight:
    /// concurrent callers join the same in-flight task.
    /// </summary>
    /// <returns>Resolved chat model id.</returns>
    public Task<string> EnsureChatModelAsync(
        string chatEndpoint,
        string? preferredModel = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Task<string> task;
        lock (_ensureLock)
        {
            if (_ensureTask == null || _ensureTask.IsCompleted)
            {
                _ensureTask = EnsureChatModelCoreAsync(chatEndpoint, preferredModel, progress, CancellationToken.None);
            }

            task = _ensureTask;
        }

        return AwaitEnsureAsync(task, cancellationToken);
    }

    private static async Task<string> AwaitEnsureAsync(Task<string> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return await task.ConfigureAwait(false);

        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completed != task)
            throw new OperationCanceledException(cancellationToken);

        return await task.ConfigureAwait(false);
    }

    private async Task<string> EnsureChatModelCoreAsync(
        string chatEndpoint,
        string? preferredModel,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(chatEndpoint)
            ? "http://localhost:13305/api/v1/chat/completions"
            : chatEndpoint.Trim();

        var targetModel = string.IsNullOrWhiteSpace(preferredModel)
            ? DefaultChatModelId
            : preferredModel.Trim();
        if (LemonadeChatClient.IsLikelyTtsModel(targetModel))
            targetModel = DefaultChatModelId;

        progress?.Report("Checking Lemonade Server…");
        if (!await _chatClient.TestConnectionAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            if (!IsLemonadeCliAvailable())
            {
                throw new InvalidOperationException(
                    "Lemonade Server is not installed. Summary mode needs it. " +
                    "Re-run Setup with “Install Lemonade Server” checked, or install from Settings / " +
                    "winget install AMD.LemonadeServer.");
            }

            progress?.Report("Starting Lemonade Server…");
            await TryStartLemonadeServerAsync(endpoint, cancellationToken).ConfigureAwait(false);

            var ready = false;
            for (var i = 0; i < 20 && !ready; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ready = await _chatClient.TestConnectionAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!ready) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            if (!ready)
            {
                throw new InvalidOperationException(
                    "Lemonade Server is not running and could not be started automatically. " +
                    "Open “Lemonade Server” from the Start menu (or run LemonadeServer.exe), then try Summary again.");
            }
        }

        var models = await _chatClient.GetModelsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var existing = LemonadeChatClient.PickChatModel(models, preferred: targetModel);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        progress?.Report($"Downloading summary model ({targetModel})…");
        AppLog.Info($"Lemonade: ensuring chat model '{targetModel}'");

        var pulled = await TryPullAndLoadViaHttpAsync(endpoint, targetModel, progress, cancellationToken).ConfigureAwait(false);
        if (!pulled)
        {
            progress?.Report($"Pulling {targetModel} via Lemonade CLI…");
            await RunLemonadeCliAsync($"pull {QuoteArg(targetModel)}", cancellationToken).ConfigureAwait(false);
            progress?.Report($"Loading {targetModel}…");
            // Prefer `load` (no browser). Fall back to `run` if load is unsupported.
            var loaded = await TryRunLemonadeCliAsync($"load {QuoteArg(targetModel)}", cancellationToken).ConfigureAwait(false);
            if (!loaded)
                await RunLemonadeCliAsync($"run {QuoteArg(targetModel)} --no-tray", cancellationToken).ConfigureAwait(false);
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        // Wait until /models lists a chat model.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            models = await _chatClient.GetModelsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            existing = LemonadeChatClient.PickChatModel(models, preferred: targetModel);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                progress?.Report($"Summary model ready ({existing})");
                return existing;
            }

            progress?.Report($"Waiting for {targetModel} to become available…");
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Lemonade still has no chat LLM after trying to install '{targetModel}'. " +
            "In a terminal run: lemonade pull " + targetModel + " && lemonade load " + targetModel);
    }

    private async Task<bool> TryPullAndLoadViaHttpAsync(
        string chatEndpoint,
        string modelName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var (chatUrl, modelsUrl) = LemonadeChatClient.ResolveEndpoints(chatEndpoint);
            var apiRoot = modelsUrl[..^"/models".Length];
            var pullUrl = apiRoot + "/pull";
            var loadUrl = apiRoot + "/load";

            progress?.Report($"Requesting Lemonade pull of {modelName}…");
            using (var pullContent = new StringContent(
                       JsonSerializer.Serialize(new { model_name = modelName }),
                       Encoding.UTF8,
                       "application/json"))
            using (var pullResponse = await _httpClient.PostAsync(pullUrl, pullContent, cancellationToken).ConfigureAwait(false))
            {
                if (!pullResponse.IsSuccessStatusCode)
                {
                    var body = await pullResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    AppLog.Warn($"Lemonade HTTP pull failed: {(int)pullResponse.StatusCode} {Truncate(body, 200)}");
                    return false;
                }
            }

            progress?.Report($"Loading {modelName} into Lemonade…");
            using (var loadContent = new StringContent(
                       JsonSerializer.Serialize(new { model_name = modelName }),
                       Encoding.UTF8,
                       "application/json"))
            using (var loadResponse = await _httpClient.PostAsync(loadUrl, loadContent, cancellationToken).ConfigureAwait(false))
            {
                if (!loadResponse.IsSuccessStatusCode)
                {
                    var body = await loadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    AppLog.Warn($"Lemonade HTTP load failed: {(int)loadResponse.StatusCode} {Truncate(body, 200)}");
                    // Pull may have succeeded; CLI load can finish the job.
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Lemonade HTTP pull/load failed; will try CLI", ex);
            return false;
        }
    }

    private Task RunLemonadeCliAsync(string arguments, CancellationToken cancellationToken) =>
        RunLemonadeCliCoreAsync(arguments, throwOnFailure: true, cancellationToken);

    private Task<bool> TryRunLemonadeCliAsync(string arguments, CancellationToken cancellationToken) =>
        RunLemonadeCliCoreAsync(arguments, throwOnFailure: false, cancellationToken);

    private async Task<bool> RunLemonadeCliCoreAsync(string arguments, bool throwOnFailure, CancellationToken cancellationToken)
    {
        var exe = FindLemonadeCliExecutable() ?? FindLemonadeServerExecutable();
        if (exe == null)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    "Lemonade CLI was not found on PATH. Install Lemonade Server, then try again.");
            }

            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            if (throwOnFailure) throw new InvalidOperationException("Failed to start Lemonade CLI.");
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = Truncate((stderr + "\n" + stdout).Trim(), 300);
            AppLog.Warn($"Lemonade CLI '{arguments}' exited {process.ExitCode}: {detail}");
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    $"Lemonade command failed ({arguments}). {detail}");
            }

            return false;
        }

        return true;
    }

    public static int? TryGetPort(string? chatEndpoint)
    {
        if (string.IsNullOrWhiteSpace(chatEndpoint)) return null;
        try
        {
            var raw = chatEndpoint.Trim();
            if (!raw.Contains("://", StringComparison.Ordinal))
                raw = "http://" + raw;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string QuoteArg(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
