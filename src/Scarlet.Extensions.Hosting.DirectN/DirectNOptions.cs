namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// Options for the DirectN application run by the host (<c>AddDirectN</c>).
/// </summary>
public sealed class DirectNOptions
{
    /// <summary>
    /// Gets or sets when the DirectN application exits on its own. Default: <see cref="ShutdownMode.OnLastWindowClose"/>.
    /// </summary>
    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnLastWindowClose;

    /// <summary>
    /// Gets or sets a callback run on the UI thread after the DirectN application is created and before the components are
    /// initialized and the message loop starts. Exceptions fail host startup.
    /// </summary>
    public Action<IServiceProvider, IDirectNContext>? OnStartup { get; set; }

    /// <summary>
    /// Gets or sets a callback run on the UI thread after the message loop has ended and the remaining windows were closed,
    /// before the components are disposed.
    /// </summary>
    public Action<IServiceProvider, IDirectNContext>? OnExit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether DirectN shows its own fatal-error dialog (a task dialog listing the errors)
    /// when a window procedure throws or an exception is unhandled. Default: <see langword="false"/>; the host logs the
    /// errors instead. Maps to DirectN's process-wide <c>Application.CanShowFatalError</c>.
    /// </summary>
    public bool ShowFatalErrorDialog { get; set; }
}
