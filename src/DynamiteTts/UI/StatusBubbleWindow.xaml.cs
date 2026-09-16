using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamiteTts.Models;
using DynamiteTts.Services;

namespace DynamiteTts.UI;

/// <summary>
/// Small always-on-top status pill (in the style of Handy's recording bubble) centred at the top of
/// the monitor you are working on. Shows "Reading selection…", "Summarizing…", "Synthesizing…" and
/// "Speaking" with animated level bars, has a stop button, and never takes keyboard focus, so it
/// cannot interfere with the Ctrl+C selection capture or with typing in the foreground app.
/// Call <see cref="ShowState"/> and <see cref="ShowNotice"/> on the UI thread.
/// </summary>
public partial class StatusBubbleWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private static readonly double[] BarProfile = { 0.55, 0.8, 1.0, 0.8, 0.55 };
    private const double BarMin = 3;
    private const double BarMax = 18;

    private readonly Func<float> _levelSource;
    private readonly Rectangle[] _bars;
    private readonly double[] _barHeights = new double[5];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _noticeTimer;

    private TrayIconState _state = TrayIconState.Idle;
    private bool _isShown;
    private bool _renderHooked;
    private int _fadeGeneration;

    /// <summary>Raised when the stop button in the bubble is clicked.</summary>
    public event Action? StopRequested;

    public StatusBubbleWindow(Func<float> levelSource)
    {
        InitializeComponent();
        _levelSource = levelSource;
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            if (_state == TrayIconState.Idle) FadeOut();
        };

        // WPF can create the native window more than once (it did when first shown), so never cache
        // the HWND: apply the no-activate style every time one is created, and look the handle up
        // whenever it is needed.
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        new WindowInteropHelper(this).EnsureHandle();

        SizeChanged += (_, _) => { if (_isShown) Reposition(); };
    }

    public TrayIconState CurrentState => _state;
    public bool IsBubbleVisible => _isShown;

    /// <summary>The current native handle (may change over the window's lifetime).</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void ApplyNoActivateStyle()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        AppLog.Info($"bubble: native window 0x{hwnd.ToInt64():X} created");
    }

    public void ShowState(TrayIconState state)
    {
        _state = state;
        switch (state)
        {
            case TrayIconState.Capturing:
                SetContent("Reading selection…", "TextSecondaryBrush", showStop: false);
                break;
            case TrayIconState.Summarizing:
                SetContent("Summarizing…", "WarningBrush", showStop: true);
                break;
            case TrayIconState.Synthesizing:
                SetContent("Synthesizing…", "WarningBrush", showStop: true);
                break;
            case TrayIconState.Speaking:
                SetContent("Speaking", "SuccessBrush", showStop: true);
                break;
            default:
                if (_noticeTimer.IsEnabled) return; // a notice is showing; its timer hides the bubble
                FadeOut(delayMs: 180);
                return;
        }

        _noticeTimer.Stop();
        FadeIn();
    }

    /// <summary>Updates the label while busy (e.g. downloading a Lemonade summary model).</summary>
    public void SetBusyText(string message)
    {
        if (_state is not (TrayIconState.Summarizing or TrayIconState.Synthesizing or TrayIconState.Capturing))
            return;
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusText.Text = message.Trim();
        FadeIn();
    }

    /// <summary>Briefly shows a message (e.g. "No text selected") when nothing else is in progress.</summary>
    public void ShowNotice(string message)
    {
        if (_state != TrayIconState.Idle) return;
        SetContent(message, "TextSecondaryBrush", showStop: false);
        FadeIn();
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void SetContent(string text, string brushKey, bool showStop)
    {
        StatusText.Text = text;
        var brush = TryFindResource(brushKey) as Brush ?? Brushes.White;
        foreach (var bar in _bars) bar.Fill = brush;
        StopButton.Visibility = showStop ? Visibility.Visible : Visibility.Collapsed;
        Pill.Padding = showStop ? new Thickness(14, 0, 6, 0) : new Thickness(14, 0, 4, 0);
    }

    private void FadeIn()
    {
        _fadeGeneration++;
        if (!_isShown)
        {
            _isShown = true;
            Show(); // ShowActivated=False + WS_EX_NOACTIVATE: never steals focus
            HookRendering(true);
        }

        Reposition();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void FadeOut(int delayMs = 0)
    {
        if (!_isShown) return;

        var generation = ++_fadeGeneration;
        var animation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            if (generation != _fadeGeneration) return; // shown again in the meantime
            _isShown = false;
            HookRendering(false);
            Hide();
        };
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>Centres the bubble at the top of the work area of the monitor holding the foreground window.</summary>
    private void Reposition()
    {
        UpdateLayout();

        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = IntPtr.Zero;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != hwnd)
            monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero && GetCursorPos(out var cursor))
            monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            AppLog.Warn($"bubble: monitor lookup failed (monitor 0x{monitor.ToInt64():X}, error {Marshal.GetLastWin32Error()}); position unchanged");
            return;
        }
        if (!GetWindowRect(hwnd, out var rect))
        {
            AppLog.Warn($"bubble: GetWindowRect failed for 0x{hwnd.ToInt64():X} (error {Marshal.GetLastWin32Error()}); position unchanged");
            return;
        }

        var width = rect.Right - rect.Left;
        var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var x = info.rcWork.Left + ((info.rcWork.Right - info.rcWork.Left) - width) / 2;
        var y = info.rcWork.Top + (int)Math.Round(6 * scale);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void HookRendering(bool on)
    {
        if (on == _renderHooked) return;
        _renderHooked = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = _clock.Elapsed.TotalSeconds;
        var level = _state == TrayIconState.Speaking ? Math.Clamp(_levelSource() * 2.4, 0, 1) : 0;

        for (int i = 0; i < _bars.Length; i++)
        {
            double target = _state switch
            {
                // Bars follow the real output level, with a little per-bar motion so they look alive.
                TrayIconState.Speaking => BarMin + (BarMax - BarMin) * level * BarProfile[i] * (0.7 + 0.3 * Math.Sin(t * 11 + i * 1.7)),
                // A travelling wave while the model renders.
                TrayIconState.Synthesizing => BarMin + (BarMax - BarMin) * 0.45 * (0.5 + 0.5 * Math.Sin(t * 6.5 - i * 0.8)),
                // A gentle ripple while the selection is copied.
                TrayIconState.Capturing => BarMin + 3 * (0.5 + 0.5 * Math.Sin(t * 4 - i * 0.6)),
                _ => BarMin
            };

            var current = _barHeights[i];
            current += (target - current) * (target > current ? 0.55 : 0.2); // fast attack, soft release
            _barHeights[i] = current;
            _bars[i].Height = current;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        HookRendering(false);
        _noticeTimer.Stop();
        base.OnClosed(e);
    }
}
