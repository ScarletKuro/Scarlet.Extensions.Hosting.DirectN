namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// Data for tray icon mouse events.
/// </summary>
/// <param name="button">The mouse button.</param>
/// <param name="clicks">The number of clicks (1 or 2) for a press; 0 for a release, a click, a move or a keyboard activation, as in WinForms.</param>
/// <param name="x">The horizontal screen coordinate.</param>
/// <param name="y">The vertical screen coordinate.</param>
public sealed class TrayMouseEventArgs(TrayMouseButtons button, int clicks, int x, int y) : EventArgs
{
    /// <summary>Gets the mouse button.</summary>
    public TrayMouseButtons Button { get; } = button;

    /// <summary>Gets the number of clicks (1 or 2) for a press; 0 for a release, a click, a move or a keyboard activation, as in WinForms.</summary>
    public int Clicks { get; } = clicks;

    /// <summary>Gets the horizontal screen coordinate, in physical pixels.</summary>
    public int X { get; } = x;

    /// <summary>Gets the vertical screen coordinate, in physical pixels.</summary>
    public int Y { get; } = y;
}
