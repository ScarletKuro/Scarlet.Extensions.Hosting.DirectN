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
/// Whoever stops the application early (<see cref="StopAndWait"/>) waits for the UI thread to end, up to the host's shutdown timeout,
/// so application cleanup doesn't run against services the host has already disposed.
/// </summary>
internal sealed unsafe partial class DirectNThread : IDirectNThread, IDisposable
{
    // Used if HostOptions are not registered; matches the HostOptions.ShutdownTimeout default.
    private const int DefaultStopTimeoutSeconds = 30;
    private const uint WM_QUIT = 0x0012;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0;

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

    // Set when the process exits while the UI thread can't run any more (see OnProcessExit).
    private volatile bool _exitingProcess;

    // Set when the host's stop already waited its whole shutdown timeout for the UI thread to end.
    private volatile bool _stopTimedOut;

    // UI thread only.
    private Application? _application;
    private DisposableList<IDirectNComponent>? _components;
    private bool _cleanedUp;

    // Errors DirectN reported and then cleared itself (its fatal error dialog clears them once shown); see CaptureErrors.
    private readonly List<Exception> _capturedErrors = [];
    private readonly Lock _errorsLock = new();
    private Func<global::DirectN.HWND, bool>? _showFatalErrorFunc;

    // How long a process exit waits for the UI thread to answer before treating it as unable to run (see OnProcessExit).
    private static readonly TimeSpan ProcessExitResponseTimeout = TimeSpan.FromSeconds(2);

    private static DirectNThread? s_running;

    private static int s_processExitRegistered;

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
    /// <remarks>
    /// Without the starting thread's ExecutionContext: AsyncLocal values, logging scopes and Activity.Current of whoever started
    /// the host would otherwise leak into every UI callback for the application's whole lifetime.
    /// </remarks>
    internal void Start() => MainThread.UnsafeStart();

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
    public void HandleApplicationExit() => StopHost();

    /// <summary>
    /// Stops the host. Until the application has started, the stop is deferred to then: stopping a host while it starts cancels
    /// its startup, which it reports as a startup failure. Never once startup has failed or the host is being disposed.
    /// </summary>
    private void StopHost()
    {
        if (_disposed || _stopRequested)
        {
            return;
        }

        var started = _started.Task;
        if (!started.IsCompleted)
        {
            started.ContinueWith(
                static (_, state) => ((DirectNThread)state!).StopHost(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            return;
        }

        if (!started.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            _serviceProvider.GetService<IHostApplicationLifetime>()?.StopApplication();
        }
        catch (ObjectDisposedException)
        {
            // The host is being disposed.
        }
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

        // Not when the host's stop already waited the shutdown timeout for it, so the host never waits for it longer than that,
        // nor while the process exits with a UI thread that can't run any more.
        if (MainThread.IsAlive && Thread.CurrentThread != MainThread && !_exitingProcess && !_stopTimedOut)
        {
            MainThread.Join(_stopTimeout);
        }
    }

    /// <summary>
    /// Called when the host's stop gave up waiting for the UI thread (its shutdown timeout elapsed).
    /// </summary>
    internal void OnStopTimedOut()
    {
        _stopTimedOut = true;
        _logger.DirectNStopTimedOut(_stopTimeout);
    }

    /// <summary>
    /// Called by the DI container when the host (its service provider) is disposed; explicit so only the container calls it.
    /// Normally the UI thread has already ended: StoppingAsync waits for it, and <see cref="DirectNStartupGuard"/> stops it if
    /// the host never finished starting. This is the last resort, bounded by the shutdown timeout like the rest.
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

    /// <summary>
    /// Registers, once per process and before any host lifetime does (it is called while services are registered), the handler
    /// that keeps the host from waiting for a UI thread that can't run any more while the process exits. ProcessExit handlers run
    /// in registration order, and DirectNLifetime's then blocks until the host has stopped.
    /// </summary>
    internal static void RegisterProcessExitHandler()
    {
        if (Interlocked.Exchange(ref s_processExitRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Volatile.Read(ref s_running)?.OnProcessExit();
        }
    }

    private void OnProcessExit()
    {
        // Environment.Exit blocks its calling thread while another thread runs the ProcessExit handlers. If that caller is the UI
        // thread, its loop never runs again: waiting for it to end would only delay the exit by the shutdown timeout. Whether it
        // still runs is only visible from here by asking it. Otherwise the host's usual stop runs, cleanup included.
        // Only while a host could still wait for it: not once disposed (e.g. Main returned after host.Run).
        if (_disposed || CurrentQueue is not { } queue || !MainThread.IsAlive)
        {
            return;
        }

        var responded = new ManualResetEventSlim();
        if (!queue.TryPost(static state => ((ManualResetEventSlim)state!).Set(), responded)
            || responded.Wait(ProcessExitResponseTimeout)
            || !MainThread.IsAlive)
        {
            return;
        }

        _exitingProcess = true;
        _context.IsRunning = false;
        _started.TrySetCanceled();
        _completed.TrySetResult(true);
        try
        {
            _logger.DirectNNotRespondingOnProcessExit();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void UiThreadStart()
    {
        Volatile.Write(ref s_running, this);
        try
        {
            RunUiThread();
        }
        finally
        {
            Interlocked.CompareExchange(ref s_running, null, this);
            lock (_stateLock)
            {
                // From now on posts are dropped instead of queued.
                _queue = null;
            }

            // Work still queued is dropped (a pending Send throws instead of waiting forever) and the window is released.
            if (DirectNMessageQueue.Current is { } queue)
            {
                queue.Close();
                if (queue.DroppedCount > 0)
                {
                    _logger.DirectNQueuedWorkDropped(queue.DroppedCount);
                }
            }

            DropPendingPosts();
            // Startup did not complete (e.g. the application exited before its loop ran): nothing left to wait for.
            _started.TrySetResult(true);
            _completed.TrySetResult(true);
        }
    }

    private void RunUiThread()
    {
        Application? application = null;
        SessionEndWindow? sessionWindow = null;
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
                if (_options.ShowFatalErrorDialog)
                {
                    // DirectN's dialog clears the errors once shown: keep them, so they are still logged (and fail startup).
                    Application.ShowFatalErrorFunc = _showFatalErrorFunc = ShowFatalError;
                }
                else
                {
                    Application.ShowFatalErrorsOnUnhandledException = false;
                }

                Application.ExitAllOnLastWindowRemoved = _options.ShutdownMode == ShutdownMode.OnLastWindowClose;
                application = new HostApplication(this)
                {
                    ExitOnLastWindowRemoved = _options.ShutdownMode == ShutdownMode.OnLastWindowClose,
                };
                _context.Application = application;
                _application = application;
                _components = components;

                // Once the end of the session is confirmed, Windows may end the process as soon as WM_ENDSESSION returns: stop then.
                sessionWindow = new SessionEndWindow(OnSessionEnd);

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

            // DirectN reported an error while initializing (a window procedure threw): fail host startup with it.
            if (TakeErrors() is { Count: > 0 } initErrors)
            {
                _started.TrySetException(initErrors.Count == 1 ? initErrors[0] : new AggregateException(initErrors));
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

            var errors = TakeErrors();
            if (errors.Count > 0)
            {
                // The loop ended because of an error before the application had started: fail host startup.
                startupFailed |= _started.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
            }

            // The application exited on its own (last window closed, Application.Exit(), an error): stop the host. When the host
            // is the one stopping, it is already stopping and this does nothing. Not when startup failed: host startup reports
            // that failure itself, and stopping now would cancel it first, hiding the exception behind a cancellation.
            if (!startupFailed)
            {
                StopHost();
            }
        }
        finally
        {
            Cleanup(application, components);
            sessionWindow?.Dispose();
        }
#pragma warning restore CA1031
    }

    private void Cleanup(Application? application, DisposableList<IDirectNComponent> components)
    {
        // Once: also run early when the session ends (OnSessionEnd).
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
#pragma warning disable CA1031 // Cleanup must reach the end so the host never waits for a UI thread that is gone; failures are logged.
        if (application is not null)
        {
            // Closing the last window must not exit again: DirectN would post WM_QUIT, which closes any message box or dialog
            // OnExit or a component's Dispose shows, as if the user had answered it.
            application.ExitOnLastWindowRemoved = false;
            Application.ExitAllOnLastWindowRemoved = false;

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

            // A quit request still pending (e.g. the host's exit arriving after the loop had already ended) would do the same.
            while (global::DirectN.Functions.PeekMessageW(out _, default, WM_QUIT, WM_QUIT, global::DirectN.PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
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

        if (_showFatalErrorFunc is not null && Application.ShowFatalErrorFunc == _showFatalErrorFunc)
        {
            Application.ShowFatalErrorFunc = null;
        }

        // Errors reported during cleanup.
        TakeErrors();
#pragma warning restore CA1031
    }

    /// <summary>
    /// The end of the Windows session is confirmed (WM_ENDSESSION, on the UI thread): Windows may end the process once this returns, so the
    /// application is stopped here and now, not through the message loop. Cleans up the application as its exit would, lets
    /// the host stop its other services meanwhile, and keeps running UI-thread work until the host has stopped.
    /// </summary>
    private void OnSessionEnd()
    {
        if (!_context.IsRunning || _application is not { } application || _components is not { } components)
        {
            return;
        }

        _logger.DirectNSessionEnding();
        _context.IsRunning = false;
        StopHost();
        Cleanup(application, components);
        _completed.TrySetResult(true);
        WaitForHostStopped();

        // WM_ENDSESSION is sent: it ran inside the loop's GetMessage, which would otherwise go on waiting for a message.
        global::DirectN.Functions.PostQuitMessage(0);
    }

    private void WaitForHostStopped()
    {
        if (_serviceProvider.GetService<IHostApplicationLifetime>() is not { } lifetime)
        {
            return;
        }

        using var stopped = new ManualResetEvent(false);
        using (lifetime.ApplicationStopped.Register(static state => ((ManualResetEvent)state!).Set(), stopped))
        {
            var deadline = Environment.TickCount64 + (long)_stopTimeout.TotalMilliseconds;
            var handle = stopped.SafeWaitHandle.DangerousGetHandle();
            while (true)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0
                    || MsgWaitForMultipleObjectsEx(1, &handle, (uint)remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_OBJECT_0)
                {
                    break;
                }

                // The other hosted services may still post (or send) to the UI thread while they stop.
                while (global::DirectN.Functions.PeekMessageW(out var msg, default, 0, 0, global::DirectN.PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    if (msg.message == WM_QUIT)
                    {
                        // The application's exit, already done.
                        continue;
                    }

                    global::DirectN.Functions.TranslateMessage(msg);
                    global::DirectN.Functions.DispatchMessageW(msg);
                }
            }
        }
    }

    /// <summary>
    /// Takes (and logs) the errors DirectN reported: those it still holds and those kept from its fatal error dialog.
    /// </summary>
    private List<Exception> TakeErrors()
    {
        List<Exception> errors;
        lock (_errorsLock)
        {
            CaptureErrors();
            Application.ClearErrors();
            errors = [.. _capturedErrors];
            _capturedErrors.Clear();
        }

        foreach (var error in errors)
        {
            _logger.DirectNError(error);
        }

        return errors;
    }

    private void CaptureErrors()
    {
        lock (_errorsLock)
        {
            foreach (var error in Application.Errors)
            {
                if (!_capturedErrors.Contains(error))
                {
                    _capturedErrors.Add(error);
                }
            }
        }
    }

    private bool HasErrors
    {
        get
        {
            lock (_errorsLock)
            {
                return _capturedErrors.Count > 0 || Application.HasErrors;
            }
        }
    }

    /// <summary>
    /// DirectN's fatal error dialog (ShowFatalErrorDialog), keeping the errors it clears once shown. May run on any thread
    /// (DirectN also shows it for an unhandled exception).
    /// </summary>
    private bool ShowFatalError(global::DirectN.HWND hwnd)
    {
        CaptureErrors();
        return Application.ShowFatalError(hwnd, configureOptions: null, callCustomFunc: false);
    }

    /// <summary>
    /// The DirectN application, ending its message loop on any DirectN error. DirectN asks for that with a thread message,
    /// which modal loops (message boxes, menus, window move/resize) discard: once such a loop returns, the error is seen here.
    /// </summary>
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    private sealed class HostApplication(DirectNThread thread) : Application
    {
        protected override global::DirectN.BOOL GetMessage(out global::DirectN.MSG msg, global::DirectN.HWND hWnd, uint wMsgFilterMin, uint wMsgFilterMax)
        {
            if (thread.HasErrors)
            {
                msg = new global::DirectN.MSG { message = (int)WM_APP_QUIT };
                return true;
            }

            return base.GetMessage(out msg, hWnd, wMsgFilterMin, wMsgFilterMax);
        }
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
        /// Runs inline on the UI thread; otherwise hands the work to the queue's Send, which returns once the work ran (or
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
