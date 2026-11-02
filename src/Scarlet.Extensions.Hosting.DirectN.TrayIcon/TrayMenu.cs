namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// The context menu of a <see cref="TrayNotifyIcon"/>. Shown as a native Win32 popup menu, rebuilt from <see cref="Items"/> each time it
/// opens, so items can be changed at any time (on the UI thread), e.g. in <see cref="Opening"/>.
/// </summary>
public sealed class TrayMenu
{
    /// <summary>
    /// Raised on the UI thread right before the menu is shown, to update the items.
    /// </summary>
    public event EventHandler? Opening;

    /// <summary>
    /// Gets the menu items.
    /// </summary>
    public IList<TrayMenuItem> Items { get; } = [];

    internal void OnOpening() => Opening?.Invoke(this, EventArgs.Empty);
}
