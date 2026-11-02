namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// When the DirectN application exits on its own (which then stops the host).
/// </summary>
public enum ShutdownMode
{
    /// <summary>
    /// Exits when the last non-background window is closed (DirectN's default). DirectN marks every window created after
    /// the first one as a background window, so by default this is when the first (main) window closes.
    /// </summary>
    OnLastWindowClose,

    /// <summary>
    /// Only exits when asked to: <see cref="IDirectNThread.HandleApplicationExit"/>, the host stopping, or
    /// <c>Application.Exit()</c>. For tray applications that run without windows.
    /// </summary>
    OnExplicitShutdown,
}
