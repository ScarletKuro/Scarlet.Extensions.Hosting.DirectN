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
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (Queue.CheckAccess())
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? error = null;
        Queue.Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        }, null);

        done.Wait();
        error?.Throw();
    }
}
