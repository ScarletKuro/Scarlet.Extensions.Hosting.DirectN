// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Scarlet.Extensions.Hosting.DirectN.Threading.Tests.Joinable;

internal static class TestUtilities
{
    /// <summary>
    /// Replacement for the upstream public <c>TplExtensions.NoThrowAwaitable</c>, which is not part of the extracted library.
    /// </summary>
    internal static ConfiguredTaskAwaitable NoThrowAwaitable(this Task task, bool captureContext = true)
        => task.ConfigureAwait(captureContext ? ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>
    /// Replacement for the upstream public <c>TplExtensions.WithTimeout</c>, which is not part of the extracted library.
    /// </summary>
    internal static Task WithTimeout(this Task task, TimeSpan timeout) => task.WaitAsync(timeout);

    /// <summary>
    /// Replacement for the upstream public <c>TplExtensions.WithTimeout{T}</c>, which is not part of the extracted library.
    /// </summary>
    internal static Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout) => task.WaitAsync(timeout);

    internal static Task SetAsync(this TaskCompletionSource<object?> tcs)
    {
        return Task.Run(() => tcs.TrySetResult(null));
    }

    /// <summary>
    /// Runs an asynchronous task synchronously, using just the current thread to execute continuations.
    /// </summary>
    internal static void Run(Func<Task> func)
    {
        if (func is null)
        {
            throw new ArgumentNullException(nameof(func));
        }

        SynchronizationContext? prevCtx = SynchronizationContext.Current;
        try
        {
            SynchronizationContext? syncCtx = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            Task? t = func();
            if (t is null)
            {
                throw new InvalidOperationException();
            }

            SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
            t.ContinueWith(_ => { frame.Continue = false; }, TaskScheduler.Default);
            SingleThreadedTestSynchronizationContext.PushFrame(syncCtx, frame);

            t.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }

    /// <summary>
    /// Executes the specified function on multiple threads simultaneously.
    /// </summary>
    /// <typeparam name="T">The type of the value returned by the specified function.</typeparam>
    /// <param name="action">The function to invoke concurrently.</param>
    /// <param name="concurrency">The level of concurrency.</param>
    internal static T[] ConcurrencyTest<T>(Func<T> action, int concurrency = -1)
    {
        Requires.NotNull(action, nameof(action));
        if (concurrency == -1)
        {
            concurrency = Environment.ProcessorCount;
        }

        Assert.SkipWhen(Environment.ProcessorCount < concurrency, $"The test machine does not have enough CPU cores to exercise a concurrency level of {concurrency}");

        // We use a barrier to guarantee that all threads are fully ready to
        // execute the provided function at precisely the same time.
        // The barrier will unblock all of them together.
        using (var barrier = new Barrier(concurrency))
        {
            var tasks = new Task<T>[concurrency];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(delegate
                {
                    barrier.SignalAndWait();
                    return action();
                });
            }

            Task.WaitAll(tasks);
            return tasks.Select(t => t.Result).ToArray();
        }
    }

    internal static DebugAssertionRevert DisableAssertionDialog()
    {
#if NETFRAMEWORK
        DefaultTraceListener? listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
        if (listener is object)
        {
            listener.AssertUiEnabled = false;
        }
#else
        Trace.Listeners.Clear();
#endif

        return default(DebugAssertionRevert);
    }

    internal static void CompleteSynchronously(this JoinableTaskFactory factory, JoinableTaskCollection collection, Task task)
    {
        Requires.NotNull(factory, nameof(factory));
        Requires.NotNull(collection, nameof(collection));
        Requires.NotNull(task, nameof(task));

        factory.Run(async delegate
        {
            using (collection.Join())
            {
                await task;
            }
        });
    }

    /// <summary>
    /// Forces an awaitable to yield, setting signals after the continuation has been pended and when the continuation has begun execution.
    /// </summary>
    /// <param name="baseAwaiter">The awaiter to extend.</param>
    /// <param name="yieldingSignal">The signal to set after the continuation has been pended.</param>
    /// <param name="resumingSignal">The signal to set when the continuation has been invoked.</param>
    /// <returns>A new awaitable.</returns>
    internal static YieldAndNotifyAwaitable YieldAndNotify(this INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal = null, AsyncManualResetEvent? resumingSignal = null)
    {
        Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

        return new YieldAndNotifyAwaitable(baseAwaiter, yieldingSignal, resumingSignal);
    }

    /// <summary>
    /// Flood the threadpool with requests that will just block the threads
    /// until the returned value is disposed of.
    /// </summary>
    /// <returns>A value to dispose of to unblock the threadpool.</returns>
    /// <remarks>
    /// This can provide a unique technique for influencing execution order
    /// of synchronous code vs. async code.
    /// </remarks>
    internal static IDisposable StarveThreadpool()
    {
        ThreadPool.GetMaxThreads(out int workerThreads, out int completionPortThreads);
        var disposalTokenSource = new CancellationTokenSource();
        var unblockThreadpool = new ManualResetEventSlim();
        for (int i = 0; i < workerThreads; i++)
        {
            Task.Run(
                () => unblockThreadpool.Wait(disposalTokenSource.Token),
                disposalTokenSource.Token);
        }

        return new DisposalAction(disposalTokenSource.Cancel);
    }

    /// <summary>
    /// Wait on a task without possibly inlining it to the current thread.
    /// </summary>
    /// <param name="task">The task to wait on.</param>
    /// <param name="throwOriginalException"><see langword="true" /> to throw the original (inner) exception when the <paramref name="task"/> faults; <see langword="false" /> to throw <see cref="AggregateException"/>.</param>
    /// <exception cref="AggregateException">Thrown if <paramref name="task"/> completes in a faulted state if <paramref name="throwOriginalException"/> is <see langword="false" />.</exception>
    internal static void WaitWithoutInlining(this Task task, bool throwOriginalException)
    {
        Requires.NotNull(task, nameof(task));
        if (!task.IsCompleted)
        {
            // Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
            Task? continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            continuation.Wait();
        }

        // Rethrow the exception if the task faulted.
        if (throwOriginalException)
        {
            task.GetAwaiter().GetResult();
        }
        else
        {
            task.Wait();
        }
    }

    /// <summary>
    /// Wait on a task without possibly inlining it to the current thread and returns its result.
    /// </summary>
    /// <typeparam name="T">The type of result returned from the <paramref name="task"/>.</typeparam>
    /// <param name="task">The task to wait on.</param>
    /// <param name="throwOriginalException"><see langword="true" /> to throw the original (inner) exception when the <paramref name="task"/> faults; <see langword="false" /> to throw <see cref="AggregateException"/>.</param>
    /// <returns>The result of the <see cref="Task{T}"/>.</returns>
    /// <exception cref="AggregateException">Thrown if <paramref name="task"/> completes in a faulted state if <paramref name="throwOriginalException"/> is <see langword="false" />.</exception>
    internal static T GetResultWithoutInlining<T>(this Task<T> task, bool throwOriginalException = true)
    {
        WaitWithoutInlining(task, throwOriginalException);
        return task.Result;
    }

    internal readonly struct YieldAndNotifyAwaitable
    {
        private readonly INotifyCompletion baseAwaiter;
        private readonly AsyncManualResetEvent? yieldingSignal;
        private readonly AsyncManualResetEvent? resumingSignal;

        internal YieldAndNotifyAwaitable(INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal, AsyncManualResetEvent? resumingSignal)
        {
            Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

            this.baseAwaiter = baseAwaiter;
            this.yieldingSignal = yieldingSignal;
            this.resumingSignal = resumingSignal;
        }

        public YieldAndNotifyAwaiter GetAwaiter()
        {
            return new YieldAndNotifyAwaiter(this.baseAwaiter, this.yieldingSignal, this.resumingSignal);
        }
    }

    internal readonly struct YieldAndNotifyAwaiter : INotifyCompletion
    {
        private readonly INotifyCompletion baseAwaiter;
        private readonly AsyncManualResetEvent? yieldingSignal;
        private readonly AsyncManualResetEvent? resumingSignal;

        internal YieldAndNotifyAwaiter(INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal, AsyncManualResetEvent? resumingSignal)
        {
            Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

            this.baseAwaiter = baseAwaiter;
            this.yieldingSignal = yieldingSignal;
            this.resumingSignal = resumingSignal;
        }

        public bool IsCompleted
        {
            get { return false; }
        }

        public void OnCompleted(Action continuation)
        {
            YieldAndNotifyAwaiter that = this;
            this.baseAwaiter.OnCompleted(delegate
            {
                if (that.resumingSignal is object)
                {
                    that.resumingSignal.Set();
                }

                continuation();
            });
            if (this.yieldingSignal is object)
            {
                this.yieldingSignal.Set();
            }
        }

        public void GetResult()
        {
        }
    }

    internal readonly struct DebugAssertionRevert : IDisposable
    {
        public void Dispose()
        {
#if NETFRAMEWORK
            DefaultTraceListener? listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
            if (listener is object)
            {
                listener.AssertUiEnabled = true;
            }
#endif
        }
    }

    private class DisposalAction : IDisposable
    {
        private readonly Action disposeAction;

        internal DisposalAction(Action disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        public void Dispose() => this.disposeAction();
    }
}
