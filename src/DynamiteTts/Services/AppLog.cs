using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DynamiteTts.Services;

/// <summary>
/// Minimal rolling file log in <c>%LocalAppData%\DynamiteTts\logs</c> (one file per day, kept for
/// a week). Records timings, engine state and failures only; never the text being spoken.
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamiteTts", "logs");

    public static string CurrentFilePath => Path.Combine(LogDirectory, $"dynamite-{DateTime.Now:yyyyMMdd}.log");

    static AppLog()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (var old in Directory.GetFiles(LogDirectory, "dynamite-*.log")
                         .Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)))
            {
                File.Delete(old);
            }
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}";
        if (ex != null) line += Environment.NewLine + "    " + ex.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");

        Debug.WriteLine(line);
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
