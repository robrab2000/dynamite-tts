using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DynamiteTts.Services;

public class LemonadeDependencyService
{
    public bool IsLemonadeCliAvailable()
    {
        return FindLemonadeExecutable() != null;
    }

    public string? FindLemonadeExecutable()
    {
        // 1. Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var p in paths)
        {
            var fullPath = Path.Combine(p, "lemonade.exe");
            if (File.Exists(fullPath)) return fullPath;

            var fullServerPath = Path.Combine(p, "lemonade-server.exe");
            if (File.Exists(fullServerPath)) return fullServerPath;
        }

        // 2. Check Standard Install Locations
        var commonDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lemonade"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LemonadeServer"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lemonade"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LemonadeServer"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "Lemonade")
        };

        foreach (var dir in commonDirs)
        {
            if (Directory.Exists(dir))
            {
                var exe = Path.Combine(dir, "lemonade.exe");
                if (File.Exists(exe)) return exe;

                var serverExe = Path.Combine(dir, "lemonade-server.exe");
                if (File.Exists(serverExe)) return serverExe;
            }
        }

        return null;
    }

    public async Task<bool> TryStartLemonadeServerAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindLemonadeExecutable();
        if (exe == null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "run kokoro-v1",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            await Task.Delay(2500, cancellationToken);
            return true;
        }
        catch
        {
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
}
