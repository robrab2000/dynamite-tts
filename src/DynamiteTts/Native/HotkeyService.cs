using System;
using System.Runtime.InteropServices;
using DynamiteTts.Models;

namespace DynamiteTts.Native;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xCAFE;

    private readonly IntPtr _hWnd;
    private HotkeyConfig? _currentConfig;
    private bool _isRegistered;
    private bool _isDisposed;

    public HotkeyConfig? CurrentConfig => _currentConfig;
    public bool IsRegistered => _isRegistered;

    public event Action<string>? HotkeyConflictOccurred;

    public HotkeyService(IntPtr hWnd)
    {
        _hWnd = hWnd;
    }

    public bool TryRegister(HotkeyConfig newConfig, out string? errorMessage)
    {
        errorMessage = null;
        if (_hWnd == IntPtr.Zero || _isDisposed)
        {
            errorMessage = "Host window handle is not available.";
            return false;
        }

        var oldConfig = _currentConfig;
        var wasRegistered = _isRegistered;

        if (wasRegistered)
        {
            NativeMethods.UnregisterHotKey(_hWnd, HotkeyId);
            _isRegistered = false;
        }

        var mods = newConfig.GetWin32Modifiers(noRepeat: true);
        var vk = newConfig.GetVirtualKey();

        var success = NativeMethods.RegisterHotKey(_hWnd, HotkeyId, mods, vk);
        if (!success)
        {
            var errorCode = Marshal.GetLastWin32Error();
            errorMessage = $"Failed to register shortcut '{newConfig}'. Error code: {errorCode}. It may be in use by another application.";
            HotkeyConflictOccurred?.Invoke(errorMessage);

            // Attempt to restore previous registration if possible
            if (wasRegistered && oldConfig != null)
            {
                var oldMods = oldConfig.GetWin32Modifiers(noRepeat: true);
                var oldVk = oldConfig.GetVirtualKey();
                if (NativeMethods.RegisterHotKey(_hWnd, HotkeyId, oldMods, oldVk))
                {
                    _isRegistered = true;
                    _currentConfig = oldConfig;
                }
            }

            return false;
        }

        _currentConfig = newConfig;
        _isRegistered = true;
        return true;
    }

    public void Unregister()
    {
        if (_isRegistered && _hWnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hWnd, HotkeyId);
            _isRegistered = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Unregister();
    }
}
