using System;
using DynamiteTts.Native;

namespace DynamiteTts.Services;

public class TrayNotificationService
{
    private readonly TrayIconHost _trayHost;

    public TrayNotificationService(TrayIconHost trayHost)
    {
        _trayHost = trayHost ?? throw new ArgumentNullException(nameof(trayHost));
    }

    public void ShowInfo(string title, string message)
    {
        _trayHost.ShowBalloonNotification(title, message, NativeMethods.NIIF_INFO);
    }

    public void ShowWarning(string title, string message)
    {
        _trayHost.ShowBalloonNotification(title, message, NativeMethods.NIIF_WARNING);
    }

    public void ShowError(string title, string message)
    {
        _trayHost.ShowBalloonNotification(title, message, NativeMethods.NIIF_ERROR);
    }
}
