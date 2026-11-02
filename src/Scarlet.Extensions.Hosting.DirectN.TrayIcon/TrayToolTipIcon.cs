namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// The icon shown in a balloon notification (<see cref="TrayNotifyIcon.ShowBalloonTip"/>).
/// </summary>
public enum TrayToolTipIcon
{
    /// <summary>No icon.</summary>
    None = 0,

    /// <summary>An information icon.</summary>
    Info = 1,

    /// <summary>A warning icon.</summary>
    Warning = 2,

    /// <summary>An error icon.</summary>
    Error = 3,
}
