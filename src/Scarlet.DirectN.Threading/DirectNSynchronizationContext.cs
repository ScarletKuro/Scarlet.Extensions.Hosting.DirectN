using System.Runtime.ExceptionServices;

namespace Scarlet.DirectN.Threading;

/// <summary>
/// <see cref="SynchronizationContext"/> that runs work on the UI thread owning a <see cref="DirectNMessageQueue"/>.
/// </summary>
/// <remarks>
/// Work is delivered through a message-only window, so it also runs while a modal loop is active
/// (menus, message boxes, task dialogs, window move/resize).
/// </remarks>
public sealed class DirectNSynchronizationContext : SynchronizationContext
{
    internal DirectNSynchronizationContext(DirectNMessageQueue queue)
    {
        Queue = queue;
    }

    /// <summary>
    /// Gets the queue this context posts to.
    /// </summary>
    public DirectNMessageQueue Queue { get; }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Queues <paramref name="d"/> to run on the UI thread. Never throws: once the queue is closed (or its thread has exited)
    /// the work is dropped, like WPF's and WinForms' contexts do after shutdown. This is the context <c>await</c> resumes on, and
    /// the runtime turns an exception thrown here into an unhandled exception that terminates the process. Use
    /// <see cref="DirectNMessageQueue.TryPost(SendOrPostCallback, object)"/> to know whether the work was queued.
    /// </summary>
    /// <param name="d">The work to run.</param>
    /// <param name="state">The state passed to <paramref name="d"/>.</param>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Queue.TryPost(d, state);
    }

    /// <summary>
    /// Runs <paramref name="d"/> on the UI thread and waits for it. Runs inline when called on the UI thread.
    /// </summary>
    /// <remarks>
    /// Blocks the calling thread until the UI thread processes the work. If the queue is closed or its thread exits before
    /// running the work, it throws instead of waiting forever. Like any Send it waits as long as the UI thread is alive but
    /// doesn't process messages (blocked, or busy with a long callback), and calling it from a thread the UI thread is itself
    /// waiting on deadlocks.
    /// </remarks>
    /// <param name="d">The work to run.</param>
    /// <param name="state">The state passed to <paramref name="d"/>.</param>
    /// <exception cref="InvalidOperationException">The queue was closed or its thread has exited before the work ran.</exception>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (Queue.CheckAccess())
        {
            d(state);
            return;
        }

        var work = new SendWork(d, state);
        if (!Queue.TryPost(static s => ((SendWork)s!).Run(), work, static s => ((SendWork)s!).Abandon()))
        {
            throw DirectNMessageQueue.QueueClosedException();
        }

        Queue.WaitForSend(work);
        work.ThrowIfFailed();
    }

    /// <summary>
    /// The queued item of a cross-thread Send. Claimed exactly once, by running it or by the queue dropping it; <see cref="Done"/>
    /// is set once that has finished (the callback returned, or the drop was recorded).
    /// </summary>
    internal sealed class SendWork(SendOrPostCallback callback, object? state)
    {
        private ExceptionDispatchInfo? _error;
        // 1 once Run or Abandon has taken the item: set before the callback runs, unlike Done.
        private int _claimed;

        public ManualResetEventSlim Done { get; } = new();

        public void Run()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return;
            }

            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                _error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Done.Set();
            }
        }

        public void Abandon()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return;
            }

            _error = ExceptionDispatchInfo.Capture(DirectNMessageQueue.QueueClosedException());
            Done.Set();
        }

        /// <summary>
        /// The owner thread has exited: if the work never finished (not queued any more, or the thread died while running it),
        /// release the caller.
        /// </summary>
        public void AbandonAfterOwnerExit()
        {
            Abandon();
            if (!Done.IsSet)
            {
                // Run had started but its thread died before it finished (e.g. ExitThread from the callback).
                _error = ExceptionDispatchInfo.Capture(DirectNMessageQueue.QueueClosedException());
                Done.Set();
            }
        }

        public void ThrowIfFailed() => _error?.Throw();
    }
}
