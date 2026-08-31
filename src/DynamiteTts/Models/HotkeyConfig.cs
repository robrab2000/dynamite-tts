using System;
using System.Text;
using System.Windows.Input;
using DynamiteTts.Native;

namespace DynamiteTts.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public class HotkeyConfig : IEquatable<HotkeyConfig>
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;
    public Key Key { get; set; } = Key.S;

    public static HotkeyConfig Default => new()
    {
        Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
        Key = Key.S
    };

    public uint GetWin32Modifiers(bool noRepeat = true)
    {
        uint mods = NativeMethods.MOD_NONE;
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= NativeMethods.MOD_ALT;
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= NativeMethods.MOD_WIN;
        if (noRepeat) mods |= NativeMethods.MOD_NOREPEAT;
        return mods;
    }

    public uint GetVirtualKey()
    {
        return (uint)KeyInterop.VirtualKeyFromKey(Key);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
        sb.Append(Key.ToString());
        return sb.ToString();
    }

    public static HotkeyConfig Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Default;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var config = new HotkeyConfig
        {
            Modifiers = HotkeyModifiers.None,
            Key = Key.None
        };

        foreach (var part in parts)
        {
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
            {
                config.Modifiers |= HotkeyModifiers.Control;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                config.Modifiers |= HotkeyModifiers.Alt;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                config.Modifiers |= HotkeyModifiers.Shift;
            }
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                config.Modifiers |= HotkeyModifiers.Win;
            }
            else if (Enum.TryParse<Key>(part, true, out var parsedKey))
            {
                config.Key = parsedKey;
            }
        }

        if (config.Key == Key.None)
            return Default;

        return config;
    }

    public bool Equals(HotkeyConfig? other)
    {
        if (other is null) return false;
        return Modifiers == other.Modifiers && Key == other.Key;
    }

    public override bool Equals(object? obj) => Equals(obj as HotkeyConfig);

    public override int GetHashCode() => HashCode.Combine(Modifiers, Key);
}
