namespace Scarlet.DirectN.Threading;

/// <summary>Schedules awaitable work on a DirectN UI thread.</summary>
/// <remarks>
/// Invocations always enqueue, even on the owning thread. Callback exceptions are observed through the returned task,
/// rather than the queue's unhandled-exception event. Cancellation only prevents a callback from starting; once started,
/// the callback owns its cancellation and completion. Never synchronously wait for an invocation on the UI thread.
/// Rejected work, or queued work abandoned by shutdown or owner-thread exit, faults with
/// <see cref="InvalidOperationException"/>. A running asynchronous callback needs a live message loop for UI continuations;
/// shutdown does not forcibly complete or cancel it. Invalid null callbacks throw synchronously.
/// </remarks>
public abstract class DirectNDispatcher
{
    /// <summary>Returns whether the calling thread owns this dispatcher.</summary>
    public abstract bool CheckAccess();

    /// <summary>Throws when called outside the owning UI thread.</summary>
    public void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("This operation must run on the DirectN UI thread.");
        }
    }

    /// <summary>Queues an action and completes when it returns.</summary>
    /// <param name="callback">The action to run on the UI thread.</param>
    /// <param name="cancellationToken">Cancels the invocation before the callback starts.</param>
    /// <returns>A task representing completion, cancellation, or failure of the invocation.</returns>
    public Task InvokeAsync(Action callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueSynchronous<object?>(() =>
        {
            callback();
            return null;
        }, cancellationToken);
    }

    /// <summary>Queues a function and completes with its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="callback">The function to run on the UI thread.</param>
    /// <param name="cancellationToken">Cancels the invocation before the callback starts.</param>
    /// <returns>A task containing the callback's result, cancellation, or failure.</returns>
    public Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueSynchronous(callback, cancellationToken);
    }

    /// <summary>Queues an asynchronous callback and awaits its full completion.</summary>
    /// <param name="callback">The callback to start on the UI thread.</param>
    /// <param name="cancellationToken">Cancels the invocation before the callback starts.</param>
    /// <returns>A task representing the full callback's completion, cancellation, or failure.</returns>
    /// <remarks>Keep the UI loop running until the callback completes if it needs UI continuations.</remarks>
    public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueAsync<object?>(async () =>
        {
            await callback().ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }

    /// <summary>Queues an asynchronous function and awaits its full result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="callback">The function to start on the UI thread.</param>
    /// <param name="cancellationToken">Cancels the invocation before the callback starts.</param>
    /// <returns>A task containing the full callback's result, cancellation, or failure.</returns>
    /// <remarks>Keep the UI loop running until the callback completes if it needs UI continuations.</remarks>
    public Task<T> InvokeAsync<T>(Func<Task<T>> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueAsync(callback, cancellationToken);
    }

    /// <summary>Queues work, with notification if accepted work is later dropped before running.</summary>
    /// <param name="callback">The callback to enqueue.</param>
    /// <param name="state">State shared by the callbacks.</param>
    /// <param name="onAbandoned">Called if accepted work is dropped without running.</param>
    /// <returns>False if the work was rejected; otherwise true.</returns>
    protected abstract bool TryEnqueue(SendOrPostCallback callback, object? state, Action<object?> onAbandoned);

    private Task<T> EnqueueSynchronous<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Enqueue(new SynchronousInvocation<T>(callback, cancellationToken));
    }

    private Task<T> EnqueueAsync<T>(Func<Task<T>> callback, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Enqueue(new AsyncInvocation<T>(callback, cancellationToken));
    }

    private Task<T> Enqueue<T>(Invocation<T> invocation)
    {
        invocation.RegisterCancellation();
        if (!TryEnqueue(static s => ((Invocation<T>)s!).Run(), invocation, static s => ((Invocation<T>)s!).Abandon()))
        {
            invocation.Abandon();
        }

        return invocation.Task;
    }

    private abstract class Invocation<T>(CancellationToken cancellationToken)
    {
        // Exactly one of running, cancellation, and abandonment may claim queued work.
        private int _claimed;
        private CancellationTokenRegistration _registration;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void RegisterCancellation()
        {
            _registration = cancellationToken.UnsafeRegister(static s => ((Invocation<T>)s!).Cancel(), this);
            // Cancellation may have run synchronously before the registration was assigned.
            if (Volatile.Read(ref _claimed) != 0)
            {
                _registration.Dispose();
            }
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                _completion.TrySetCanceled(cancellationToken);
                _registration.Unregister();
            }
        }

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }

            _registration.Dispose();
            try
            {
                InvokeCore();
            }
            catch (Exception ex)
            {
                CompleteException(ex);
            }
        }

        protected abstract void InvokeCore();

        protected void Complete(T result) => _completion.TrySetResult(result);

        protected void CompleteException(Exception exception)
        {
            if (exception is OperationCanceledException canceled)
            {
                _completion.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                _completion.TrySetException(exception);
            }
        }

        public void Abandon()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                _registration.Dispose();
                _completion.TrySetException(DirectNMessageQueue.QueueClosedException());
            }
        }
    }

    private sealed class SynchronousInvocation<T>(Func<T> callback, CancellationToken cancellationToken)
        : Invocation<T>(cancellationToken)
    {
        protected override void InvokeCore() => Complete(callback());
    }

    private sealed class AsyncInvocation<T>(Func<Task<T>> callback, CancellationToken cancellationToken)
        : Invocation<T>(cancellationToken)
    {
        protected override void InvokeCore() => _ = CompleteAsync();

        private async Task CompleteAsync()
        {
            try
            {
                Complete(await callback().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                CompleteException(ex);
            }
        }
    }
}
