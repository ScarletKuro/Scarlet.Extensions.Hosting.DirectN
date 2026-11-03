namespace Scarlet.DirectN.Threading;

/// <summary>Schedules work on a DirectN UI thread, with optional completion tracking.</summary>
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

    /// <summary>Queues an action on the UI thread without waiting for completion.</summary>
    /// <param name="callback">The action to enqueue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The dispatcher no longer accepts work.</exception>
    /// <remarks>
    /// Always queues, including on the owning thread. Accepted work may still be dropped during shutdown.
    /// Callback exceptions use the UI thread's unhandled-exception path. Use InvokeAsync for completion tracking
    /// or asynchronous callbacks.
    /// </remarks>
    public void Post(Action callback)
    {
        if (!TryPost(callback))
        {
            throw DirectNMessageQueue.QueueClosedException();
        }
    }

    /// <summary>Tries to queue an action on the UI thread without waiting for completion.</summary>
    /// <param name="callback">The action to enqueue.</param>
    /// <returns>
    /// False only when the action cannot run because the dispatcher no longer accepts work. True means it was accepted;
    /// accepted work may still be dropped during shutdown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
    /// <remarks>
    /// Always queues, including on the owning thread. Callback exceptions use the UI thread's unhandled-exception path.
    /// Use InvokeAsync for completion tracking or asynchronous callbacks.
    /// </remarks>
    public bool TryPost(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return TryEnqueue(static state => ((Action)state!)(), callback, static _ => { });
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
    /// <returns>False only if the callback cannot run; otherwise true.</returns>
    /// <remarks>
    /// Implementations must be thread-safe and always enqueue work, never invoke it inline. Preserve the caller's
    /// ExecutionContext and install the owning UI thread's SynchronizationContext while running the callback.
    /// If returning true, eventually invoke exactly one of callback or onAbandoned with state, even on shutdown or
    /// owner-thread exit. Callback execution and abandonment must be mutually exclusive. Abandonment may be reported
    /// before this method returns, including when it returns false; callers must tolerate that notification.
    /// Expected shutdown or rejection must return false rather than throw. Route callback exceptions through the
    /// UI thread's unhandled-exception path and never let them escape a native callback boundary.
    /// </remarks>
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
