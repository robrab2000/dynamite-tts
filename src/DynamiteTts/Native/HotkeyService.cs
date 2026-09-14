using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DynamiteTts.Models;

namespace DynamiteTts.Native;

public sealed class HotkeyService : IDisposable
{
    public const int SpeakHotkeyId = 0xCAFE;
    public const int ModeToggleHotkeyId = 0xCAFF;

    private readonly IntPtr _hWnd;
    private readonly Dictionary<int, HotkeyConfig> _registered = new();
    private bool _isDisposed;

    public HotkeyConfig? SpeakConfig => _registered.TryGetValue(SpeakHotkeyId, out var c) ? c : null;
    public HotkeyConfig? ModeToggleConfig => _registered.TryGetValue(ModeToggleHotkeyId, out var c) ? c : null;
    public bool IsSpeakRegistered => _registered.ContainsKey(SpeakHotkeyId);
    public bool IsModeToggleRegistered => _registered.ContainsKey(ModeToggleHotkeyId);

    /// <summary>Backward-compatible alias for the speak hotkey registration.</summary>
    public HotkeyConfig? CurrentConfig => SpeakConfig;
    public bool IsRegistered => IsSpeakRegistered;

    public event Action<string>? HotkeyConflictOccurred;

    public HotkeyService(IntPtr hWnd)
    {
        _hWnd = hWnd;
    }

    public bool TryRegisterPair(HotkeyConfig speak, HotkeyConfig modeToggle, out string? errorMessage)
    {
        errorMessage = null;
        if (speak.Equals(modeToggle))
        {
            errorMessage = "The speak shortcut and the mode-toggle shortcut must be different.";
            HotkeyConflictOccurred?.Invoke(errorMessage);
            return false;
        }

        var previous = Snapshot();

        UnregisterAll();

        if (!TryRegisterInternal(SpeakHotkeyId, speak, out errorMessage))
        {
            Restore(previous);
            return false;
        }

        if (!TryRegisterInternal(ModeToggleHotkeyId, modeToggle, out errorMessage))
        {
            Restore(previous);
            return false;
        }

        return true;
    }

    /// <summary>Registers only the speak hotkey (tests / legacy). Prefer <see cref="TryRegisterPair"/>.</summary>
    public bool TryRegister(HotkeyConfig newConfig, out string? errorMessage)
    {
        var modeToggle = ModeToggleConfig ?? HotkeyConfig.ModeToggleDefault;
        if (newConfig.Equals(modeToggle) && IsModeToggleRegistered)
        {
            errorMessage = "The speak shortcut conflicts with the mode-toggle shortcut.";
            HotkeyConflictOccurred?.Invoke(errorMessage);
            return false;
        }

        return TryRegisterPair(newConfig, modeToggle, out errorMessage);
    }

    public void Unregister()
    {
        UnregisterAll();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        UnregisterAll();
    }

    private bool TryRegisterInternal(int id, HotkeyConfig config, out string? errorMessage)
    {
        errorMessage = null;
        if (_hWnd == IntPtr.Zero || _isDisposed)
        {
            errorMessage = "Host window handle is not available.";
            return false;
        }

        var mods = config.GetWin32Modifiers(noRepeat: true);
        var vk = config.GetVirtualKey();

        if (!NativeMethods.RegisterHotKey(_hWnd, id, mods, vk))
        {
            var errorCode = Marshal.GetLastWin32Error();
            errorMessage = $"Failed to register shortcut '{config}'. Error code: {errorCode}. It may be in use by another application.";
            HotkeyConflictOccurred?.Invoke(errorMessage);
            return false;
        }

        _registered[id] = new HotkeyConfig { Modifiers = config.Modifiers, Key = config.Key };
        return true;
    }

    private void UnregisterAll()
    {
        if (_hWnd == IntPtr.Zero) return;

        foreach (var id in _registered.Keys)
        {
            NativeMethods.UnregisterHotKey(_hWnd, id);
        }

        _registered.Clear();
    }

    private Dictionary<int, HotkeyConfig> Snapshot()
    {
        var copy = new Dictionary<int, HotkeyConfig>();
        foreach (var (id, config) in _registered)
        {
            copy[id] = new HotkeyConfig { Modifiers = config.Modifiers, Key = config.Key };
        }
        return copy;
    }

    private void Restore(Dictionary<int, HotkeyConfig> previous)
    {
        UnregisterAll();
        foreach (var (id, config) in previous)
        {
            TryRegisterInternal(id, config, out _);
        }
    }
}
