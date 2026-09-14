using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DynamiteTts.Models;
using DynamiteTts.UI;
using Xunit;
using Xunit.Abstractions;

namespace DynamiteTts.Tests;

/// <summary>
/// Shows the real status bubble on screen in each state, saves screenshots to
/// experiments/bubble-shots, and checks it is centred at the top of the monitor and never becomes
/// the foreground window. Puts a window on the desktop for a few seconds, so it is tagged Perf.
/// </summary>
[Trait("Category", "Perf")]
public class StatusBubbleVisualTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private readonly ITestOutputHelper _output;

    public StatusBubbleVisualTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Bubble_ShowsEachState_CentredAtTop_WithoutTakingFocus()
    {
        var shots = Path.Combine(FindRepoRoot(AppContext.BaseDirectory) ?? Path.GetTempPath(), "experiments", "bubble-shots");
        Directory.CreateDirectory(shots);

        RunOnDispatcher(async dispatcher =>
        {
            // Never construct DynamiteTts.App here: WPF runs its OnStartup, whose single-instance check
            // finds a running Dynamite TTS, tells it to open Settings, and shuts this dispatcher's
            // application down (closing the bubble). Use a plain Application with the theme brushes
            // the bubble looks up (values from App.xaml).
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["TextSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF));
                app.Resources["WarningBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                app.Resources["SuccessBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            }

            var clock = Stopwatch.StartNew();
            var bubble = new StatusBubbleWindow(() => (float)(0.22 + 0.18 * Math.Sin(clock.Elapsed.TotalSeconds * 7)));
            IntPtr hwnd = IntPtr.Zero;

            var steps = new (string Name, Action Show)[]
            {
                ("1-reading-selection", () => bubble.ShowState(TrayIconState.Capturing)),
                ("2-synthesizing", () => bubble.ShowState(TrayIconState.Synthesizing)),
                ("3-speaking", () => bubble.ShowState(TrayIconState.Speaking)),
            };

            foreach (var (name, show) in steps)
            {
                show();
                await Task.Delay(800); // fade-in plus some animation frames
                hwnd = bubble.Handle;
                Assert.True(bubble.IsBubbleVisible, $"{name}: bubble should be visible");
                Assert.NotEqual(hwnd, GetForegroundWindow());
                CheckPlacement(hwnd, name);
                Capture(hwnd, Path.Combine(shots, name + ".png"), context: false);
            }

            // Placement is verified numerically in CheckPlacement; no wide screenshots of the desktop.

            bubble.ShowState(TrayIconState.Idle);
            await Task.Delay(700);
            Assert.False(bubble.IsBubbleVisible, "bubble should hide when idle");

            bubble.ShowNotice("No text selected");
            await Task.Delay(500);
            Assert.True(bubble.IsBubbleVisible, "notice should show the bubble");
            hwnd = bubble.Handle;
            Capture(hwnd, Path.Combine(shots, "4-notice.png"), context: false);
            await Task.Delay(1700);
            Assert.False(bubble.IsBubbleVisible, "notice should hide on its own");

            bubble.Close();
            _output.WriteLine($"screenshots: {shots}");
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRectWithError(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private void CheckPlacement(IntPtr hwnd, string step)
    {
        var rectOk = GetWindowRectWithError(hwnd, out var r);
        var rectError = Marshal.GetLastWin32Error();
        _output.WriteLine($"{step}: hwnd=0x{hwnd.ToInt64():X} isWindow={IsWindow(hwnd)} visible={IsWindowVisible(hwnd)} getWindowRect={rectOk} error={rectError} rect={r.Left},{r.Top},{r.Right},{r.Bottom}");
        Assert.True(rectOk, $"{step}: GetWindowRect failed for the bubble (hwnd 0x{hwnd.ToInt64():X}, error {rectError})");

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        Assert.True(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info), $"{step}: GetMonitorInfo failed");

        var work = info.rcWork;
        var bubbleCentre = (r.Left + r.Right) / 2.0;
        var workCentre = (work.Left + work.Right) / 2.0;
        _output.WriteLine($"{step}: window {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}; work area {work.Left},{work.Top}-{work.Right},{work.Bottom}; centre offset {bubbleCentre - workCentre:F1}px, top offset {r.Top - work.Top}px");

        Assert.InRange(bubbleCentre - workCentre, -2, 2);
        Assert.InRange(r.Top - work.Top, 0, 40);
    }

    private static void Capture(IntPtr hwnd, string path, bool context)
    {
        GetWindowRect(hwnd, out var r);
        int left, top, width, height;
        if (context)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
            left = info.rcMonitor.Left;
            top = info.rcMonitor.Top;
            width = info.rcMonitor.Right - info.rcMonitor.Left;
            height = Math.Max(160, r.Bottom - info.rcMonitor.Top + 60);
        }
        else
        {
            left = r.Left - 40;
            top = Math.Max(0, r.Top - 10);
            width = (r.Right - r.Left) + 80;
            height = (r.Bottom - r.Top) + 30;
        }

        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string? FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "DynamiteTts.sln"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private static void RunOnDispatcher(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(dispatcher); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(1)))
            throw new TimeoutException("Dispatcher test did not finish within 1 minute.");
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
