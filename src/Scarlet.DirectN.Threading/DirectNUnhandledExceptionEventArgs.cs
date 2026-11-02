namespace Scarlet.DirectN.Threading;

/// <summary>
/// Provides data for the <see cref="DirectNMessageQueue.UnhandledException"/> event.
/// </summary>
public sealed class DirectNUnhandledExceptionEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DirectNUnhandledExceptionEventArgs"/> class.
    /// </summary>
    /// <param name="exception">The exception thrown by work dispatched to the UI thread.</param>
    public DirectNUnhandledExceptionEventArgs(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <summary>
    /// Gets the exception that was thrown by work dispatched to the UI thread.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the exception has been handled. When left <see langword="false"/>,
    /// the exception is rethrown as an unhandled exception, which terminates the process
    /// (matching the default behavior of WinForms and WPF).
    /// </summary>
    public bool Handled { get; set; }
}
