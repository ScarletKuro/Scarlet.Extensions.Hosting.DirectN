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

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Queue.Post(d, state);
    }

    /// <summary>
    /// Runs <paramref name="d"/> on the UI thread and waits for it. Runs inline when called on the UI thread.
    /// </summary>
    /// <remarks>
    /// Blocks the calling thread until the UI thread processes the work. Calling it from a thread the UI thread
    /// is itself waiting on deadlocks.
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
        if (!Queue.TryPost(static s => ((SendWork)s!).Run(), work))
        {
            throw SendWork.QueueClosed();
        }

        work.Wait();
    }

    internal sealed class SendWork(SendOrPostCallback callback, object? state)
    {
        private readonly ManualResetEventSlim _done = new();
        private ExceptionDispatchInfo? _error;

        public static InvalidOperationException QueueClosed() => new("The DirectN UI thread's queue is closed; the work did not run.");

        public void Run()
        {
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
                _done.Set();
            }
        }

        public void Abandon()
        {
            _error = ExceptionDispatchInfo.Capture(QueueClosed());
            _done.Set();
        }

        public void Wait()
        {
            _done.Wait();
            _error?.Throw();
        }
    }
}
