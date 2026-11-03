using Scarlet.DirectN.Threading;

namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// The DirectN UI thread run by the host.
/// </summary>
public interface IDirectNThread
{
    /// <summary>
    /// Gets the UI thread. It exists (unstarted) before the host starts, so it can be handed to e.g. a
    /// <c>JoinableTaskContext</c> while services are being constructed.
    /// </summary>
    Thread MainThread { get; }

    /// <summary>
    /// Gets the UI dispatcher for posting work and awaiting invocations. Calls made before startup are buffered until
    /// the application starts; shutdown or failed startup faults invocations that have not started. Do not await invocations inside
    /// a hosted service's StartAsync: the UI thread starts only after every StartAsync has returned.
    /// </summary>
    DirectNDispatcher Dispatcher { get; }

    /// <summary>
    /// Gets a <see cref="SynchronizationContext"/> that runs work on the UI thread. Usable at any time: work posted before the
    /// UI thread has started runs once it has started; work posted after it has ended is dropped (and logged), never thrown.
    /// </summary>
    SynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Gets the state of the DirectN application.
    /// </summary>
    IDirectNContext Context { get; }

    /// <summary>
    /// Stops the host, which exits the DirectN application. Callable from any thread, e.g. from a tray icon's Exit item.
    /// Before the application has started (e.g. from <c>OnStartup</c>), the host is stopped once it has: stopping a host
    /// while it starts would fail its startup. Ignored when startup failed or the host is being disposed.
    /// </summary>
    void HandleApplicationExit();

    /// <summary>
    /// Raised on the UI thread when work posted to it throws. Set <see cref="DirectNUnhandledExceptionEventArgs.Handled"/>
    /// to keep the application running; otherwise the exception is logged and terminates the process
    /// (like WPF's <c>DispatcherUnhandledException</c>).
    /// </summary>
    event EventHandler<DirectNUnhandledExceptionEventArgs>? UnhandledException;
}
