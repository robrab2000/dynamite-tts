using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DynamiteTts.Models;

namespace DynamiteTts.Native;

public sealed class TrayIconHost : IDisposable
{
    private const uint TrayIconId = 1;

    private const int CmdStop = 1001;
    private const int CmdSettings = 1002;
    private const int CmdRefreshModels = 1003;
    private const int CmdExit = 1004;

    private readonly string _windowClassName;
    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly uint _singleInstanceMsg;
    private IntPtr _hWnd;
    private bool _isDisposed;

    private IntPtr _hIconIdle;
    private IntPtr _hIconActive;
    private IntPtr _hIconSpeaking;

    public event Action? HotkeyPressed;
    public event Action? StopRequested;
    public event Action? SettingsRequested;
    public event Action? RefreshModelsRequested;
    public event Action? ExitRequested;

    public IntPtr Handle => _hWnd;

    public TrayIconHost()
    {
        _windowClassName = $"DynamiteTts_TrayHost_{Guid.NewGuid():N}";
        _wndProcDelegate = CustomWndProc;
        _singleInstanceMsg = NativeMethods.RegisterWindowMessage("DynamiteTts_ShowSettings");

        LoadIcons();
        CreateHostWindow();
        AddTrayIcon();
    }

    private void LoadIcons()
    {
        var baseDir = AppContext.BaseDirectory;
        var idlePath = Path.Combine(baseDir, "Resources", "app_idle.ico");
        var activePath = Path.Combine(baseDir, "Resources", "app_active.ico");
        var speakingPath = Path.Combine(baseDir, "Resources", "app_speaking.ico");

        _hIconIdle = LoadIconFromFile(idlePath);
        _hIconActive = LoadIconFromFile(activePath);
        _hIconSpeaking = LoadIconFromFile(speakingPath);

        if (_hIconIdle == IntPtr.Zero)
        {
            // Fallback to application icon
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                _hIconIdle = System.Drawing.Icon.ExtractAssociatedIcon(exePath)?.Handle ?? IntPtr.Zero;
            }
        }
    }

    private static IntPtr LoadIconFromFile(string path)
    {
        if (!File.Exists(path)) return IntPtr.Zero;
        try
        {
            using var icon = new System.Drawing.Icon(path);
            return icon.Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void CreateHostWindow()
    {
        var hInstance = NativeMethods.GetModuleHandle(null);

        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProcDelegate,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = _windowClassName,
            hIconSm = IntPtr.Zero
        };

        var regResult = NativeMethods.RegisterClassEx(ref wndClass);
        var regError = Marshal.GetLastWin32Error();

        _hWnd = NativeMethods.CreateWindowEx(
            0,
            _windowClassName,
            "DynamiteTts_HiddenHost",
            NativeMethods.WS_OVERLAPPED,
            0, 0, 0, 0,
            new IntPtr(NativeMethods.HWND_MESSAGE),
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hWnd == IntPtr.Zero)
        {
            var createError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to create message-only tray window. ClassRegResult: {regResult}, RegError: {regError}, CreateError: {createError}");
        }
    }

    private void AddTrayIcon()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _hIconIdle,
            szTip = "Dynamite TTS (Ready)"
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid);
    }

    public void SetState(TrayIconState state)
    {
        if (_hWnd == IntPtr.Zero || _isDisposed) return;

        var (hIcon, tip) = state switch
        {
            TrayIconState.Synthesizing => (_hIconActive != IntPtr.Zero ? _hIconActive : _hIconIdle, "Dynamite TTS (Synthesizing...)"),
            TrayIconState.Speaking => (_hIconSpeaking != IntPtr.Zero ? _hIconSpeaking : _hIconActive, "Dynamite TTS (Speaking...)"),
            _ => (_hIconIdle, "Dynamite TTS (Ready)")
        };

        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            hIcon = hIcon,
            szTip = tip
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
    }

    public void ShowBalloonNotification(string title, string message, uint infoFlags = NativeMethods.NIIF_INFO)
    {
        if (_hWnd == IntPtr.Zero || _isDisposed) return;

        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = message.Length > 255 ? message[..255] : message,
            dwInfoFlags = infoFlags
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdStop, "Stop\tEsc");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdSettings, "Settings...");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdRefreshModels, "Refresh Models");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, CmdExit, "Exit");

            // Set Settings as default bold item
            NativeMethods.SetMenuDefaultItem(hMenu, CmdSettings, 0);

            NativeMethods.SetForegroundWindow(_hWnd);

            var cmd = NativeMethods.TrackPopupMenu(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                pt.x,
                pt.y,
                0,
                _hWnd,
                IntPtr.Zero);

            NativeMethods.PostMessage(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdStop:
                    StopRequested?.Invoke();
                    break;
                case CmdSettings:
                    SettingsRequested?.Invoke();
                    break;
                case CmdRefreshModels:
                    RefreshModelsRequested?.Invoke();
                    break;
                case CmdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _singleInstanceMsg && _singleInstanceMsg != 0)
        {
            SettingsRequested?.Invoke();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case NativeMethods.WM_TRAYICON:
                var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == NativeMethods.WM_RBUTTONUP || mouseMsg == NativeMethods.WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                }
                else if (mouseMsg == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    SettingsRequested?.Invoke();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke();
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hWnd != IntPtr.Zero)
        {
            var nid = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hWnd,
                uID = TrayIconId
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid);

            NativeMethods.DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
    }
}
