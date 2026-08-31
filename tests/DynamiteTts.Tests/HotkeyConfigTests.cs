using System.Windows.Input;
using DynamiteTts.Models;
using Xunit;

namespace DynamiteTts.Tests;

public class HotkeyConfigTests
{
    [Fact]
    public void DefaultHotkey_IsCtrlShiftS()
    {
        var config = HotkeyConfig.Default;

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, config.Modifiers);
        Assert.Equal(Key.S, config.Key);
        Assert.Equal("Ctrl+Shift+S", config.ToString());
    }

    [Fact]
    public void Parse_ValidString_ReturnsCorrectConfig()
    {
        var parsed = HotkeyConfig.Parse("Alt+Shift+F9");

        Assert.Equal(HotkeyModifiers.Alt | HotkeyModifiers.Shift, parsed.Modifiers);
        Assert.Equal(Key.F9, parsed.Key);
    }

    [Fact]
    public void Parse_InvalidString_FallsBackToDefault()
    {
        var parsed = HotkeyConfig.Parse("");
        Assert.Equal(HotkeyConfig.Default, parsed);
    }
}
