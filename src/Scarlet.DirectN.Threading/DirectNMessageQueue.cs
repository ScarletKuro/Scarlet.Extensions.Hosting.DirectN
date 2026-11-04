using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DirectN;
using Microsoft.Win32.SafeHandles;

namespace Scarlet.DirectN.Threading;

/// <summary>
/// A per-thread work queue for a Win32 UI thread, drained by the thread's message loop.
/// </summary>
/// <remarks>
/// <para>
/// Work is queued in memory and a single wake-up message is posted to a message-only window owned by the UI thread.
/// Window messages are dispatched by every message loop on the thread, including modal ones (menus, message boxes,
/// task dialogs, window move/resize), so queued work never stalls while one of those is open. Thread messages
/// (<c>PostThreadMessage</c>) would be lost in those loops.
/// </para>
/// <para>
/// There is at most one queue per thread (<see cref="GetOrCreateForCurrentThread"/>), so every component on a UI thread
/// (the host, other libraries, the app) shares the same ordering, exception path and <see cref="SynchronizationContext"/>.
/// </para>
/// <para>
/// Guarantees: <see cref="TryPost(SendOrPostCallback, object)"/> returns <see langword="false"/> only when the work can never run (the queue was closed or
/// its thread has exited); transient failures such as a full thread message queue are retried. A cross-thread
/// <see cref="DirectNSynchronizationContext.Send"/> returns once the work ran (while the thread processes messages), or throws
/// <see cref="InvalidOperationException"/> because the queue was closed or its thread exited first.
/// </para>
/// </remarks>
public sealed unsafe partial class DirectNMessageQueue : DirectNDispatcher
{
    private const string WakeMessageName = "Scarlet.DirectN.Threading.DirectNMessageQueue";

    // Unique per copy of this library in the process (e.g. two Native AOT DLLs), so each copy has its own window procedure.
    private static readonly string WindowClassName = WakeMessageName + "." + Guid.NewGuid().ToString("N");

    // Upper bound of work items run per wake-up so a flood of posts can't starve input, paint and timer messages.
    private const int MaxItemsPerWakeUp = 256;

    // And at most about a frame of work per wake-up: 256 items of a few milliseconds each would keep input waiting for seconds.
    private static readonly long MaxTicksPerWakeUp = System.Diagnostics.Stopwatch.Frequency * 15 / 1000;

    // The timer that continues a backlog once pending input, paint and timer messages have had their turn.
    private const nuint ContinueTimerId = 1;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint QS_INPUT_PAINT_TIMER = 0x0407 | 0x0020 | 0x0010;

    // Parent handle that makes CreateWindowEx create a message-only window.
    private const nint HWND_MESSAGE = -3;

    private const uint SYNCHRONIZE = 0x00100000;

    // Delay between attempts to post the wake-up message when the thread's message queue is full.
    private static readonly TimeSpan WakeRetryDelay = TimeSpan.FromMilliseconds(10);

    private static readonly uint WakeMessage = RegisterWakeMessage();
    private static readonly Lock ClassRegistrationLock = new();
    private static bool _classRegistered;

    [ThreadStatic]
    private static DirectNMessageQueue? t_current;

    private readonly ConcurrentQueue<WorkItem> _queue = new();

    // The UI thread's own context, captured when the queue is created on it (or, if flow was suppressed then, when first
    // needed). Work posted with ExecutionContext flow
    // suppressed runs in it, so that work is isolated from other callbacks too.
    private ExecutionContext? _baselineContext;
    private readonly HWND _hwnd;
    private readonly OwnerThreadExitHandle _ownerExit;
    private readonly RegisteredWaitHandle _ownerExitRegistration;
    private readonly Timer _wakeRetry;
    private GCHandle _self;

    // 1 while a wake-up message is posted (or being retried) and not processed yet.
    private int _wakePending;

    private int _closeReason;
    private int _dropped;

    // 1 once closed. Written with Interlocked (a full fence) so that, together with the fence of ConcurrentQueue.Enqueue,
    // a TryPost racing with closing either sees the queue closed or has its item dropped by the closing thread.
    private int _closed;
    // Owner-thread only: logical closure may happen on another thread before native cleanup is requested.
    private bool _resourcesReleased;
    private bool _windowDestroyed;

    private DirectNMessageQueue(Action<ConstructionStage, HWND>? constructionStageReached = null)
    {
        ManagedThreadId = Environment.CurrentManagedThreadId;
        Thread = Thread.CurrentThread;
        SynchronizationContext = new DirectNSynchronizationContext(this);
        _baselineContext = ExecutionContext.Capture();
        // Every queue must observe owner exit, including when no more posts arrive. Fail construction if the handle
        // cannot be opened rather than accepting invocations that could remain incomplete after unexpected thread exit.
        _ownerExit = new OwnerThreadExitHandle(OpenOwnerThreadHandle(Functions.GetCurrentThreadId()));
        try
        {
            constructionStageReached?.Invoke(ConstructionStage.OwnerHandleOpened, default);
            _wakeRetry = new Timer(static state => ((DirectNMessageQueue)state!).RetryWake(), this, Timeout.Infinite, Timeout.Infinite);
            EnsureWindowClassRegistered();

            fixed (char* className = WindowClassName)
            {
                _hwnd = Functions.CreateWindowExW(
                    0,
                    new PWSTR { Value = (nint)className },
                    default,
                    0,
                    0, 0, 0, 0,
                    new HWND(HWND_MESSAGE),
                    default,
                    new HINSTANCE { Value = Functions.GetModuleHandleW(default).Value },
                    0);
            }

            if (_hwnd.Value == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            constructionStageReached?.Invoke(ConstructionStage.WindowCreated, _hwnd);
            // Weak: the thread-static keeps the queue alive while its thread lives; owner-exit monitoring keeps it alive
            // until queued work has been abandoned if the thread exits without Close.
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            Functions.SetWindowLongPtrW(_hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, GCHandle.ToIntPtr(_self));
            constructionStageReached?.Invoke(ConstructionStage.UserDataAttached, _hwnd);
            _ownerExitRegistration = ThreadPool.UnsafeRegisterWaitForSingleObject(_ownerExit,
                static (state, _) => ((DirectNMessageQueue)state!).StopAcceptingWork(QueueCloseReason.OwnerThreadExited),
                this, Timeout.Infinite, executeOnlyOnce: true);
            constructionStageReached?.Invoke(ConstructionStage.ExitMonitorRegistered, _hwnd);
        }
        catch
        {
            // Roll back on the creating thread. Detach native callbacks before freeing their managed handle.
            _ownerExitRegistration?.Unregister(null);
            _wakeRetry?.Dispose();
            if (_hwnd.Value != 0)
            {
                Functions.SetWindowLongPtrW(_hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                Functions.DestroyWindow(_hwnd);
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _ownerExit.Dispose();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    /// <summary>
    /// Raised on the UI thread when queued work throws. Set <see cref="DirectNUnhandledExceptionEventArgs.Handled"/>
    /// to keep the process alive; otherwise the exception is rethrown as an unhandled exception.
    /// </summary>
    public event EventHandler<DirectNUnhandledExceptionEventArgs>? UnhandledException;

    /// <summary>
    /// Gets the managed thread id of the UI thread that owns this queue.
    /// </summary>
    public int ManagedThreadId { get; }

    /// <summary>
    /// Gets the UI thread that owns this queue.
    /// </summary>
    public Thread Thread { get; }

    /// <summary>
    /// Gets the <see cref="SynchronizationContext"/> that posts to this queue.
    /// </summary>
    public DirectNSynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Gets a value indicating whether the queue no longer accepts work: it was closed, or its thread has exited.
    /// </summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || !Thread.IsAlive;

    /// <summary>
    /// Gets the queue of the current thread, or <see langword="null"/> when the thread has none.
    /// </summary>
    public static DirectNMessageQueue? Current => t_current;

    /// <summary>
    /// Gets the queue of the current thread, creating it (and its message-only window) on first use.
    /// Must be called on the UI thread, which must run a message loop for queued work to execute.
    /// </summary>
    /// <returns>The queue of the current thread.</returns>
    /// <exception cref="Win32Exception">The owner-thread handle or message-only window could not be created.</exception>
    public static DirectNMessageQueue GetOrCreateForCurrentThread() => t_current ??= new DirectNMessageQueue();

    /// <summary>
    /// Returns whether the calling thread is the UI thread that owns this queue.
    /// </summary>
    /// <returns><see langword="true"/> on the owning thread.</returns>
    public override bool CheckAccess() => Environment.CurrentManagedThreadId == ManagedThreadId;

    /// <summary>
    /// Queues <paramref name="callback"/> to run on the UI thread.
    /// </summary>
    /// <param name="callback">The work to run.</param>
    /// <param name="state">The state passed to <paramref name="callback"/>.</param>
    /// <exception cref="InvalidOperationException">The queue was closed or its thread has exited.</exception>
    public void Post(SendOrPostCallback callback, object? state)
    {
        if (!TryPost(callback, state))
        {
            throw QueueClosedException();
        }
    }

    /// <summary>
    /// Queues <paramref name="callback"/> to run on the UI thread.
    /// </summary>
    /// <param name="callback">The work to run.</param>
    /// <param name="state">The state passed to <paramref name="callback"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the work can never run: the queue was closed or its thread has exited. Work accepted
    /// while the queue is being closed may still be dropped by the closing.
    /// </returns>
    public bool TryPost(SendOrPostCallback callback, object? state) => TryPostAbandonable(callback, state, onAbandoned: null);

    /// <inheritdoc />
    protected override bool TryEnqueue(SendOrPostCallback callback, object? state, Action<object?> onAbandoned) =>
        TryPostAbandonable(callback, state, onAbandoned);

    /// <summary>
    /// Queues <paramref name="callback"/>, with what to do if it is dropped without running (closing the queue, or the owner
    /// thread exiting): a cross-thread Send uses it to release its caller.
    /// </summary>
    internal bool TryPostAbandonable(SendOrPostCallback callback, object? state, Action<object?>? onAbandoned)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (IsClosed)
        {
            return false;
        }

        var item = new WorkItem(callback, state, ExecutionContext.Capture(), onAbandoned);
        _queue.Enqueue(item);
        PostStageReachedForTests?.Invoke(PostStage.Enqueued);

        // Closed concurrently (see _closed): the closing thread may already have drained the queue, so drop what is left here.
        // The item may still have run first: the owner thread can take it while draining, before closing.
        if (Volatile.Read(ref _closed) != 0)
        {
            AbandonQueuedWork();
            return !item.WasAbandoned();
        }

        PostStageReachedForTests?.Invoke(PostStage.Waking);

        // A failed wake-up means the queue was found closed or its thread gone, and everything still queued was dropped. The
        // owner thread may have taken this item before that (and closed the queue itself, destroying the window the wake-up
        // was posted to), so only report what really happened to it.
        return Wake() || !item.WasAbandoned();
    }

    /// <summary>
    /// Closes the queue: work still queued is dropped (pending <see cref="DirectNSynchronizationContext.Send"/> calls throw),
    /// later posts are rejected, and the message-only window and native handle are released. Call it on the owning thread
    /// when its message loop has ended for good. Does nothing if already closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from another thread.</exception>
    public void Close()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("A DirectNMessageQueue can only be closed by its own thread.");
        }

        if (_resourcesReleased)
        {
            return;
        }

        _resourcesReleased = true;
        // 1. Logical closing may already have happened, but owner-thread cleanup must still run once.
        StopAcceptingWork(QueueCloseReason.Closed);

        // 2. Release the native resources: only here, on the owner thread, and only once.
        _ownerExitRegistration.Unregister(null);
        _wakeRetry.Dispose();
        if (!_windowDestroyed)
        {
            Functions.SetWindowLongPtrW(_hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
            Functions.DestroyWindow(_hwnd);
            _windowDestroyed = true;
        }

        _self.Free();
        // Not _ownerExit: a Send accepted before closing may still be about to wait on it (its callback may even have closed the
        // queue). Its SafeHandle releases the thread handle once the queue is collected.
        GC.SuppressFinalize(this);
        if (t_current == this)
        {
            t_current = null;
        }
    }

    /// <summary>
    /// Finalizes the queue of a thread that exited without <see cref="Close"/> (its window went with the thread).
    /// </summary>
    ~DirectNMessageQueue()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>
    /// Gets the number of work items the queue dropped without running them, because it was closed (or its thread exited) first.
    /// </summary>
    public int DroppedCount => Volatile.Read(ref _dropped);

    /// <summary>
    /// Tests only: called on the posting thread at each <see cref="PostStage"/> of TryPost, so a test can make the owner thread
    /// act (e.g. close the queue) at that exact point. The races there are a few instructions wide and can't be forced from
    /// outside. Never set in production; it costs one null check per post.
    /// </summary>
    internal Action<PostStage>? PostStageReachedForTests;

    internal HWND WindowHandleForTests => _hwnd;

    // Per-construction fault injection, without mutable process-wide hooks or reflection (also runs under Native AOT).
    internal static DirectNMessageQueue GetOrCreateForCurrentThreadForTests(Action<ConstructionStage, HWND> stageReached) =>
        t_current ??= new DirectNMessageQueue(stageReached);

    internal enum ConstructionStage
    {
        OwnerHandleOpened,
        WindowCreated,
        UserDataAttached,
        ExitMonitorRegistered,
    }

    internal enum PostStage
    {
        Enqueued,
        Waking,
    }

    internal static InvalidOperationException QueueClosedException() =>
        new("The DirectN UI thread's queue is closed or its thread has exited; the work did not run.");

    /// <summary>
    /// Blocks until <paramref name="send"/> has completed, or until the owning thread exits; then whatever is still queued is
    /// dropped (which releases a pending Send). Used by cross-thread Send.
    /// </summary>
    internal void WaitForSend(DirectNSynchronizationContext.SendWork send)
    {
        var done = send.Done;
        if (WaitHandle.WaitAny([done.WaitHandle, _ownerExit]) == 0)
        {
            return;
        }

        // The owner exited without finishing the work, so it never will.
        StopAcceptingWork(QueueCloseReason.OwnerExitedDuringSend);
        send.AbandonAfterOwnerExit();
    }

    private bool Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 1)
        {
            // A wake-up is already in flight (or being retried) and will pick up this item too, unless the thread is gone.
            return !CloseIfOwnerGone();
        }

        if (Functions.PostMessageW(_hwnd, WakeMessage, default, default))
        {
            return true;
        }

        if (CloseIfOwnerGone())
        {
            return false;
        }

        // The thread is alive but its message queue is full: keep _wakePending set and retry until it accepts the message.
        _wakeRetry.Change(WakeRetryDelay, Timeout.InfiniteTimeSpan);
        return true;
    }

    private void RetryWake()
    {
        if (Volatile.Read(ref _closed) != 0 || CloseIfOwnerGone())
        {
            return;
        }

        if (!Functions.PostMessageW(_hwnd, WakeMessage, default, default))
        {
            // Returns false (doesn't throw) if Close disposed the timer in the meantime.
            _wakeRetry.Change(WakeRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private bool CloseIfOwnerGone()
    {
        var threadAlive = Thread.IsAlive;
        if (threadAlive && Functions.IsWindow(_hwnd))
        {
            return false;
        }

        StopAcceptingWork(threadAlive ? QueueCloseReason.WindowGone : QueueCloseReason.OwnerThreadExited);
        return true;
    }

    /// <summary>
    /// Gets why the queue stopped accepting work (the first reason), for diagnostics. <see cref="QueueCloseReason.None"/>
    /// while open; <see cref="IsClosed"/> can also be true without a reason once the owner thread has exited.
    /// </summary>
    internal QueueCloseReason CloseReason => (QueueCloseReason)Volatile.Read(ref _closeReason);

    /// <summary>
    /// Logical closing, callable from any thread (by <see cref="Close"/>, or when the owner thread is found gone): rejects all
    /// later work and abandons what is queued. Never touches the native resources; only <see cref="Close"/> releases those,
    /// on the owner thread.
    /// </summary>
    /// <returns><see langword="true"/> for the call that closed the queue.</returns>
    private bool StopAcceptingWork(QueueCloseReason reason)
    {
        var first = Interlocked.Exchange(ref _closed, 1) == 0;
        if (first)
        {
            Volatile.Write(ref _closeReason, (int)reason);
        }

        AbandonQueuedWork();
        return first;
    }

    /// <summary>
    /// Removes everything queued without running it, telling each item it was abandoned (a pending cross-thread Send then
    /// throws, so its caller never waits forever); posted work is simply dropped.
    /// </summary>
    private void AbandonQueuedWork()
    {
        while (_queue.TryDequeue(out var item))
        {
            item.MarkAbandoned();
            Interlocked.Increment(ref _dropped);
            item.Abandon();
        }
    }

    private void ProcessQueue()
    {
        Interlocked.Exchange(ref _wakePending, 0);

        var processed = 0;
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + MaxTicksPerWakeUp;
        while (processed < MaxItemsPerWakeUp && Volatile.Read(ref _closed) == 0 && _queue.TryDequeue(out var item))
        {
            item.MarkRunning();
            processed++;
            Execute(item);

            // A single long item can't be interrupted; the budget only decides when to stop starting new ones.
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                break;
            }
        }

        if (Volatile.Read(ref _closed) == 0 && !_queue.IsEmpty)
        {
            // More work than one wake-up's worth (items or time). Posted messages are retrieved before input, paint and timer messages, so
            // another wake-up message would starve those: when any is pending, continue from a timer instead (retrieved after
            // all of them), as WPF does for background work.
            if ((GetQueueStatus(QS_INPUT_PAINT_TIMER) >> 16) != 0)
            {
                YieldToPendingMessages();
                return;
            }

            // Never fails while the thread runs (a full message queue is retried).
            Wake();
        }
    }

    private void YieldToPendingMessages()
    {
        // From now on posts don't post a wake-up message (the timer picks their work up), and one a post made during the drain
        // already posted is removed: it would be retrieved before the input it should yield to.
        Interlocked.Exchange(ref _wakePending, 1);
        while (Functions.PeekMessageW(out _, _hwnd, WakeMessage, WakeMessage, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE | PEEK_MESSAGE_REMOVE_TYPE.PM_NOYIELD))
        {
        }

        if (Functions.SetTimer(_hwnd, ContinueTimerId, 0, null) != 0)
        {
            return;
        }

        // No timer available: post the wake-up message after all (_wakePending is already set, so Wake would skip it).
        if (!Functions.PostMessageW(_hwnd, WakeMessage, default, default) && !CloseIfOwnerGone())
        {
            _wakeRetry.Change(WakeRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Execute(WorkItem item)
    {
        var previous = System.Threading.SynchronizationContext.Current;
        try
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(SynchronizationContext);

            // Like WPF's DispatcherOperation and WinForms' Control.BeginInvoke: the callback runs in the ExecutionContext
            // captured from its caller (AsyncLocal values, culture), and whatever it changes there doesn't leak into the next one.
            var context = item.Context ?? (_baselineContext ??= ExecutionContext.Capture());
            if (context is null)
            {
                // Flow is suppressed on the UI thread itself: nothing to run in.
                item.Invoke();
            }
            else
            {
                ExecutionContext.Run(context, static s => ((WorkItem)s!).Invoke(), item);
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the native window procedure.
            ReportUnhandledException(ex);
        }
        finally
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void ReportUnhandledException(Exception exception)
    {
        try
        {
            var args = new DirectNUnhandledExceptionEventArgs(exception);
            UnhandledException?.Invoke(this, args);
            if (args.Handled)
            {
                return;
            }
        }
        catch (Exception handlerException)
        {
            exception = new AggregateException(exception, handlerException);
        }

        RethrowAsUnhandled(exception);
    }

    /// <summary>
    /// Rethrows <paramref name="exception"/> on a thread-pool thread, so it surfaces as an unhandled exception
    /// (and terminates the process) without crossing a native callback boundary.
    /// </summary>
    /// <param name="exception">The exception to rethrow.</param>
    public static void RethrowAsUnhandled(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var edi = ExceptionDispatchInfo.Capture(exception);
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), edi);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetQueueStatus(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenThread(uint dwDesiredAccess, int bInheritHandle, uint dwThreadId);

    internal static SafeWaitHandle OpenOwnerThreadHandle(uint threadId)
    {
        var handle = OpenThread(SYNCHRONIZE, 0, threadId);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SafeWaitHandle(handle, ownsHandle: true);
    }

    private static uint RegisterWakeMessage()
    {
        uint message;
        fixed (char* name = WakeMessageName)
        {
            message = Functions.RegisterWindowMessageW(new PWSTR { Value = (nint)name });
        }

        if (message == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return message;
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (ClassRegistrationLock)
        {
            if (_classRegistered)
            {
                return;
            }

            fixed (char* className = WindowClassName)
            {
                var wc = new WNDCLASSW
                {
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                    hInstance = new HINSTANCE { Value = Functions.GetModuleHandleW(default).Value },
                    lpszClassName = new PWSTR { Value = (nint)className },
                };

                if (Functions.RegisterClassW(wc) == 0)
                {
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ERROR_CLASS_ALREADY_EXISTS)
                    {
                        throw new Win32Exception(error);
                    }
                }
            }

            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            var continueTimer = msg == WM_TIMER && (nuint)wParam == ContinueTimerId;
            if (continueTimer)
            {
                Functions.KillTimer(new HWND(hwnd), ContinueTimerId);
            }

            if (msg == WakeMessage || continueTimer || msg == WM_NCDESTROY)
            {
                var userData = Functions.GetWindowLongPtrW(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
                if (msg == WM_NCDESTROY)
                {
                    Functions.SetWindowLongPtrW(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                }

                if (userData != 0 && GCHandle.FromIntPtr(userData).Target is DirectNMessageQueue queue)
                {
                    if (msg == WM_NCDESTROY)
                    {
                        queue._windowDestroyed = true;
                        queue.StopAcceptingWork(QueueCloseReason.WindowGone);
                    }
                    else
                    {
                        queue.ProcessQueue();
                    }
                }

                return 0;
            }

            return Functions.DefWindowProcW(new HWND(hwnd), msg, new WPARAM { Value = (nuint)wParam }, new LPARAM { Value = lParam }).Value;
        }
        catch (Exception ex)
        {
            // Cover the entire native callback, including managed-handle lookup and destruction notifications.
            RethrowAsUnhandled(ex);
            return 0;
        }
    }

    private sealed class WorkItem(SendOrPostCallback callback, object? state, ExecutionContext? context, Action<object?>? onAbandoned)
    {
        /// <summary>
        /// Gets the caller's context, or <see langword="null"/> when the caller suppressed ExecutionContext flow.
        /// </summary>
        public ExecutionContext? Context => context;

        // What became of the item once taken from the queue (never changes after): run, or dropped without running.
        private const int Queued = 0, Running = 1, Abandoned = 2;
        private int _fate;

        public void Invoke() => callback(state);

        public void MarkRunning() => Volatile.Write(ref _fate, Running);

        public void MarkAbandoned() => Volatile.Write(ref _fate, Abandoned);

        /// <summary>
        /// Whether the item was dropped. Only called once the queue holds it no longer (it has been drained): whoever took it
        /// marks it right away, so this waits at most for that.
        /// </summary>
        public bool WasAbandoned()
        {
            var spinner = default(SpinWait);
            while (Volatile.Read(ref _fate) == Queued)
            {
                spinner.SpinOnce();
            }

            return Volatile.Read(ref _fate) == Abandoned;
        }

        /// <summary>
        /// Dropped without running.
        /// </summary>
        public void Abandon() => onAbandoned?.Invoke(state);
    }

    private sealed class OwnerThreadExitHandle : WaitHandle
    {
        public OwnerThreadExitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}
