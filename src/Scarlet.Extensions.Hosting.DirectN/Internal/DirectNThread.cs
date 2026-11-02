using DirectN.Extensions.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scarlet.DirectN.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.Internal;

/// <summary>
/// The UI thread: creates the message queue, the DirectN <see cref="Application"/> and the components, runs the message loop,
/// and reports the application as started once the loop runs (<see cref="Started"/>). Failures until then fail host startup.
/// Whoever stops the application early (<see cref="StopAndWait"/>) waits for the UI thread to end, so application cleanup never
/// runs against services the host has already disposed.
/// </summary>
internal sealed class DirectNThread : IDirectNThread, IDisposable
{
    // Used if HostOptions are not registered; matches the HostOptions.ShutdownTimeout default.
    private const int DefaultStopTimeoutSeconds = 30;

    // How long stopping the application early waits for the UI thread to end: the host's shutdown timeout. Read once, when the
    // thread is created, because the service provider may already be disposing when it is needed.
    private readonly TimeSpan _stopTimeout;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DirectNThread> _logger;
    private readonly DirectNOptions _options;
    private readonly DirectNContext _context;
    private readonly HostSynchronizationContext _synchronizationContext;

    // Guards _disposed, _stopRequested and IsRunning together, so a stopped thread can never start the message loop.
    // Also guards _pendingPosts and _queue.
    private readonly Lock _stateLock = new();

    // Work posted to the UI thread (see Post) goes through three phases:
    // - before the UI thread has created its message queue: kept in _pendingPosts;
    // - during startup (_queue set, the loop has not run the started marker yet): the list is held, new posts go to the queue;
    // - once the loop runs (OnApplicationStarted): the list is flushed into the queue and _pendingPosts is null.
    // Older work waits while newer posts run on purpose: the list comes from outside (e.g. a request accepted while the host was
    // starting) and should find the application started, while a post made during startup may be what a modal loop in a
    // component's InitializeComponent is waiting for, so holding it could deadlock startup.
    private List<PendingPost>? _pendingPosts = [];
    private DirectNMessageQueue? _queue;
    private volatile bool _disposed;
    private volatile bool _stopRequested;

    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DirectNThread(IServiceProvider serviceProvider, IOptions<DirectNOptions> options, DirectNContext context)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _context = context;
        _logger = serviceProvider.GetService<ILogger<DirectNThread>>() ?? NullLogger<DirectNThread>.Instance;
        _stopTimeout = serviceProvider.GetService<IOptions<HostOptions>>()?.Value.ShutdownTimeout ?? TimeSpan.FromSeconds(DefaultStopTimeoutSeconds);
        _synchronizationContext = new HostSynchronizationContext(this);

        MainThread = new Thread(UiThreadStart)
        {
            Name = "DirectN UI Thread",
            IsBackground = true,
        };
        MainThread.SetApartmentState(ApartmentState.STA);
    }

    public event EventHandler<DirectNUnhandledExceptionEventArgs>? UnhandledException;

    public Thread MainThread { get; }

    public SynchronizationContext SynchronizationContext => _synchronizationContext;

    public IDirectNContext Context => _context;

    /// <summary>
    /// Completes once the application and its components are initialized and the message loop runs.
    /// Faults with the original exception if creating, initializing or starting it fails.
    /// </summary>
    internal Task Started => _started.Task;

    /// <summary>
    /// Completes when the UI thread has ended: the loop exited, windows were closed, OnExit ran and the components were disposed.
    /// Already completed if the thread was never started.
    /// </summary>
    internal Task Completed => (MainThread.ThreadState & ThreadState.Unstarted) != 0 ? Task.CompletedTask : _completed.Task;

    /// <summary>
    /// Starts the UI thread. Called by <see cref="DirectNHostedService"/> at the right point of the host lifecycle.
    /// </summary>
    internal void Start() => MainThread.Start();

    /// <summary>
    /// Gets the UI thread's queue while it exists (from its creation until the UI thread ends), else <see langword="null"/>.
    /// </summary>
    internal DirectNMessageQueue? CurrentQueue
    {
        get
        {
            lock (_stateLock)
            {
                return _queue;
            }
        }
    }

    /// <inheritdoc />
    public void HandleApplicationExit()
    {
        // Stopped by the library because the host failed to start or is being disposed: the host must not be stopped
        // (it is not running, and its service provider may already be disposing).
        if (_disposed || _stopRequested || !_context.IsRunning)
        {
            return;
        }

        _serviceProvider.GetService<IHostApplicationLifetime>()?.StopApplication();
    }

    /// <summary>
    /// Posts to the UI thread. Before the UI thread exists (it starts after every hosted service has started, so e.g. a
    /// request a server accepts during startup can get here first), the work is kept and runs once the message loop runs.
    /// Never throws: JoinableTaskFactory terminates the process (Environment.FailFast) when posting to the UI thread throws.
    /// </summary>
    /// <returns><see langword="false"/> when the work was dropped because the UI thread has ended (or never started).</returns>
    internal bool Post(SendOrPostCallback callback, object? state)
    {
        DirectNMessageQueue? queue = null;
        var firstQueued = false;
        lock (_stateLock)
        {
            if (_queue is null)
            {
                if (_pendingPosts is not { } pendingPosts)
                {
                    // The UI thread ended (or the host was disposed) before it created its queue.
                    LogPostDropped();
                    return false;
                }

                firstQueued = pendingPosts.Count == 0;
                pendingPosts.Add(new PendingPost(callback, state, ExecutionContext.Capture()));
            }
            else
            {
                queue = _queue;
            }
        }

        if (queue is null)
        {
            if (firstQueued)
            {
                _logger.DirectNPostQueued();
            }

            return true;
        }

        if (!queue.TryPost(callback, state))
        {
            LogPostDropped();
            return false;
        }

        return true;
    }

    private void LogPostDropped()
    {
        // Posting must never throw, also once the host (and its logger factory) has been disposed.
        if (_disposed)
        {
            return;
        }

        try
        {
            _logger.DirectNPostDropped();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Exits the message loop. Used by the host's stop (<see cref="DirectNHostedService"/>).
    /// </summary>
    /// <returns><see langword="false"/> when the application wasn't running.</returns>
    internal bool RequestExit()
    {
        DirectNMessageQueue? queue;
        Application? application;
        lock (_stateLock)
        {
            if (!_context.IsRunning)
            {
                return false;
            }

            queue = _queue;
            application = _context.Application;
        }

        if (queue is null || application is null)
        {
            return false;
        }

        if (queue.CheckAccess())
        {
            ExitApplication(application);
        }
        else if (!queue.TryPost(static state => ((DirectNThread)state!).ExitRunningApplication(), this))
        {
            // The UI thread is already gone.
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the application without the host's usual stop (host startup failed or was cancelled, or the host is being
    /// disposed), or prevents it from running if it is still initializing, and waits for the UI thread to end.
    /// </summary>
    internal void StopAndWait()
    {
        lock (_stateLock)
        {
            _stopRequested = true;
        }

        if (RequestExit() && _queue?.CheckAccess() == true)
        {
            // Called on the UI thread itself: it cannot wait for its own end.
            return;
        }

        if (MainThread.IsAlive && Thread.CurrentThread != MainThread)
        {
            MainThread.Join(_stopTimeout);
        }
    }

    /// <summary>
    /// Called by the DI container when the host (its service provider) is disposed; explicit so only the container calls it.
    /// Normally the UI thread has already ended: StoppingAsync waits for it, and <see cref="DirectNStartupGuard"/> stops it if
    /// the host never finished starting. This is the last resort: no UI thread may outlive the host.
    /// </summary>
    void IDisposable.Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAndWait();
        // The UI thread was never started (host startup failed before DirectN, or was never attempted).
        DropPendingPosts();
    }

    private void ExitRunningApplication()
    {
        if (_context is { IsRunning: true, Application: { } application })
        {
            ExitApplication(application);
        }
    }

    private void ExitApplication(Application application)
    {
        // The host is stopping: an ApplicationExiting handler can't veto it.
        if (!application.Exit())
        {
            _logger.DirectNExitCancelled();
            global::DirectN.Functions.PostQuitMessage(0);
        }
    }

    /// <summary>
    /// Work posted before the UI thread existed that can no longer run. Whoever awaited it (e.g.
    /// JoinableTaskFactory.SwitchToMainThreadAsync) keeps waiting, which is still better than a process terminated without a trace.
    /// </summary>
    private void DropPendingPosts()
    {
        int count;
        lock (_stateLock)
        {
            count = _pendingPosts?.Count ?? 0;
            _pendingPosts = null;
        }

        if (count > 0)
        {
            _logger.DirectNQueuedPostsDropped(count);
        }
    }

    private void UiThreadStart()
    {
        try
        {
            RunUiThread();
        }
        finally
        {
            lock (_stateLock)
            {
                // From now on posts are dropped instead of queued.
                _queue = null;
            }

            // Work still queued is dropped (a pending Send throws instead of waiting forever) and the window is released.
            DirectNMessageQueue.Current?.Close();
            DropPendingPosts();
            // Startup did not complete (e.g. the application exited before its loop ran): nothing left to wait for.
            _started.TrySetResult(true);
            _completed.TrySetResult(true);
        }
    }

    private void RunUiThread()
    {
        Application? application = null;
        // Disposed when the UI thread ends, also if a later component fails to initialize.
        var components = new DisposableList<IDirectNComponent>();
#pragma warning disable CA1031 // Not swallowed: rethrown on the host thread by DirectNHostedService.StartedAsync via Started, or logged.
        try
        {
            DirectNMessageQueue queue;
            try
            {
                queue = DirectNMessageQueue.GetOrCreateForCurrentThread();
                queue.UnhandledException += OnQueueUnhandledException;
                SynchronizationContext.SetSynchronizationContext(queue.SynchronizationContext);

                // Without the dialog, a window procedure error must make DirectN record it and quit the loop (the host then logs
                // it and stops); otherwise DirectN shows its task dialog from the window procedure, whatever CanShowFatalError says.
                Application.CanShowFatalError = _options.ShowFatalErrorDialog;
                if (!_options.ShowFatalErrorDialog)
                {
                    Application.ShowFatalErrorsOnUnhandledException = false;
                }
                Application.ExitAllOnLastWindowRemoved = _options.ShutdownMode == ShutdownMode.OnLastWindowClose;
                application = new Application
                {
                    ExitOnLastWindowRemoved = _options.ShutdownMode == ShutdownMode.OnLastWindowClose,
                };
                _context.Application = application;

                lock (_stateLock)
                {
                    // From now on new posts go straight to the queue.
                    _queue = queue;
                }

                _options.OnStartup?.Invoke(_serviceProvider, _context);
                InitializeComponents(components);
            }
            catch (Exception exception)
            {
                // An exception escaping this background thread would kill the process without being logged.
                _started.TrySetException(exception);
                return;
            }

            lock (_stateLock)
            {
                // Stopped or disposed while initializing (e.g. startup was cancelled): never run the loop without a host.
                if (_disposed || _stopRequested)
                {
                    _started.TrySetCanceled();
                    return;
                }

                _context.IsRunning = true;
            }

            // The first queued work the loop runs (after anything posted during startup): the application has started.
            queue.Post(static state => ((DirectNThread)state!).OnApplicationStarted(), this);

            var startupFailed = false;
            try
            {
                // Blocks until the application exits.
                application.Run();
            }
            catch (Exception exception) when (!_started.Task.IsCompleted)
            {
                startupFailed = _started.TrySetException(exception);
            }
            finally
            {
                _context.IsRunning = false;
            }

            var errors = Application.GetErrors(clear: true);
            foreach (var error in errors)
            {
                _logger.DirectNError(error);
            }

            if (errors.Count > 0)
            {
                // The loop ended because of an error before the application had started: fail host startup.
                startupFailed |= _started.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
            }

            // The application exited on its own (last window closed, Application.Exit(), an error): stop the host. When the host
            // is the one stopping, it is already stopping and this does nothing. Not when startup failed: host startup reports
            // that failure itself, and stopping now would cancel it first, hiding the exception behind a cancellation.
            if (!startupFailed && !_disposed && !_stopRequested)
            {
                _serviceProvider.GetService<IHostApplicationLifetime>()?.StopApplication();
            }
        }
        finally
        {
            Cleanup(application, components);
        }
#pragma warning restore CA1031
    }

    private void Cleanup(Application? application, DisposableList<IDirectNComponent> components)
    {
#pragma warning disable CA1031 // Cleanup must reach the end so the host never waits for a UI thread that is gone; failures are logged.
        if (application is not null)
        {
            // Like WPF's Application.Shutdown: windows still open are closed before the exit callback runs.
            foreach (var window in application.Windows)
            {
                try
                {
                    window.Dispose();
                }
                catch (Exception exception)
                {
                    _logger.DirectNUnhandledException(exception);
                }
            }

            try
            {
                _options.OnExit?.Invoke(_serviceProvider, _context);
            }
            catch (Exception exception)
            {
                _logger.DirectNUnhandledException(exception);
            }
        }

        try
        {
            components.Dispose();
        }
        catch (AggregateException exception)
        {
            foreach (var inner in exception.InnerExceptions)
            {
                _logger.DirectNUnhandledException(inner);
            }
        }

        try
        {
            application?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.DirectNUnhandledException(exception);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Runs on the UI thread as the first queued work of the message loop.
    /// </summary>
    private void OnApplicationStarted()
    {
        try
        {
            // Resolved after everything resolved during startup (OnStartup, components), so the container, which disposes in
            // reverse creation order, disposes it before those services: if the host never finishes starting, it stops the
            // application while they still work.
            _serviceProvider.GetRequiredService<DirectNStartupGuard>();
        }
        catch (ObjectDisposedException)
        {
            // The host is already being disposed.
            _started.TrySetCanceled();
            ExitRunningApplication();
            return;
        }

        FlushPendingPosts();
        _started.TrySetResult(true);
    }

    /// <summary>
    /// Forwards the work posted before the UI thread existed, now that the application has started (see Post).
    /// </summary>
    private void FlushPendingPosts()
    {
        List<PendingPost>? pendingPosts;
        DirectNMessageQueue? queue;
        lock (_stateLock)
        {
            pendingPosts = _pendingPosts;
            _pendingPosts = null;
            queue = _queue;
        }

        if (queue is null || pendingPosts is null)
        {
            return;
        }

        foreach (var post in pendingPosts)
        {
            // In the poster's ExecutionContext, captured when it posted, not the UI thread's at flush time.
            var posted = post.Context is null
                ? queue.TryPost(post.Callback, post.State)
                : queue.TryPost(static s => ((PendingPost)s!).RunInCapturedContext(), post);
            if (!posted)
            {
                LogPostDropped();
            }
        }
    }

    private sealed class PendingPost(SendOrPostCallback callback, object? state, ExecutionContext? context)
    {
        public SendOrPostCallback Callback => callback;

        public object? State => state;

        public ExecutionContext? Context => context;

        /// <summary>
        /// Runs the callback in the poster's ExecutionContext. Only used when one was captured (flow wasn't suppressed).
        /// </summary>
        public void RunInCapturedContext()
        {
            ExecutionContext.Run(context!, static state => ((PendingPost)state!).Invoke(), this);
        }

        private void Invoke() => callback(state);
    }

    private void InitializeComponents(DisposableList<IDirectNComponent> components)
    {
        foreach (var factory in _serviceProvider.GetServices<Func<IDirectNComponent>>())
        {
            var component = factory();
            // Track it before initializing, so it is disposed even if InitializeComponent throws.
            components.Add(component);
            component.InitializeComponent();
        }
    }

    private void OnQueueUnhandledException(object? sender, DirectNUnhandledExceptionEventArgs e)
    {
        UnhandledException?.Invoke(this, e);
        if (!e.Handled)
        {
            // The queue rethrows it next, which terminates the process: leave a trace first.
            _logger.DirectNUnhandledException(e.Exception);
        }
    }

    /// <summary>
    /// The context handed out as <see cref="IDirectNThread.SynchronizationContext"/>: usable before the UI thread exists.
    /// </summary>
    private sealed class HostSynchronizationContext(DirectNThread thread) : SynchronizationContext
    {
        public override SynchronizationContext CreateCopy() => this;

        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            thread.Post(d, state);
        }

        /// <summary>
        /// Runs inline on the UI thread; otherwise hands the work to the queue's Send, which always returns (the work ran, or
        /// it throws because the queue closed first). Before the UI thread has created its queue it throws instead of
        /// blocking: a Send from a hosted service's StartAsync would otherwise deadlock, the UI thread starting after it.
        /// </summary>
        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            if (Thread.CurrentThread == thread.MainThread)
            {
                d(state);
                return;
            }

            if (thread.CurrentQueue is not { } queue)
            {
                throw new InvalidOperationException(
                    "Send is only available while the DirectN UI thread runs. Before it has started use Post or JoinableTaskFactory.SwitchToMainThreadAsync.");
            }

            queue.SynchronizationContext.Send(d, state);
        }
    }
}
