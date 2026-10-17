using System.Windows.Input;
using DynamiteTts.Models;
using DynamiteTts.Native;
using Xunit;

namespace DynamiteTts.Tests;

public class HotkeyServiceTests
{
    [Fact]
    public void TryRegisterPair_RejectsIdenticalShortcuts()
    {
        using var trayHost = new TrayIconHost();
        using var service = new HotkeyService(trayHost.Handle);

        var speak = HotkeyConfig.Default;
        var same = new HotkeyConfig { Modifiers = speak.Modifiers, Key = speak.Key };

        var ok = service.TryRegisterPair(speak, same, out var error);

        Assert.False(ok);
        Assert.Contains("must be different", error);
        Assert.False(service.IsSpeakRegistered);
        Assert.False(service.IsModeToggleRegistered);
    }

    [Fact]
    public void TryRegisterPair_RegistersBothHotkeys()
    {
        using var trayHost = new TrayIconHost();
        using var service = new HotkeyService(trayHost.Handle);

        var speak = new HotkeyConfig
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            Key = Key.F21
        };
        var toggle = new HotkeyConfig
        {
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            Key = Key.F22
        };

        var ok = service.TryRegisterPair(speak, toggle, out var error);

        Assert.True(ok, error);
        Assert.True(service.IsSpeakRegistered);
        Assert.True(service.IsModeToggleRegistered);
        Assert.Equal(speak, service.SpeakConfig);
        Assert.Equal(toggle, service.ModeToggleConfig);
    }
}
