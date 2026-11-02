// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// Extension methods and awaitables for .NET 4.5.
/// </summary>
internal static partial class AwaitExtensions
{
    /// <summary>
    /// Gets an awaiter that schedules continuations on the specified scheduler.
    /// </summary>
    /// <param name="scheduler">The task scheduler used to execute continuations.</param>
    /// <returns>An awaitable.</returns>
    public static TaskSchedulerAwaiter GetAwaiter(this TaskScheduler scheduler)
    {
        Requires.NotNull(scheduler, nameof(scheduler));
        return new TaskSchedulerAwaiter(scheduler);
    }

    /// <summary>
    /// Gets an awaitable that schedules continuations on the specified scheduler.
    /// </summary>
    /// <param name="scheduler">The task scheduler used to execute continuations.</param>
    /// <param name="alwaysYield">A value indicating whether the caller should yield even if
    /// already executing on the desired task scheduler.</param>
    /// <returns>An awaitable.</returns>
    public static TaskSchedulerAwaitable SwitchTo(this TaskScheduler scheduler, bool alwaysYield = false)
    {
        Requires.NotNull(scheduler, nameof(scheduler));
        return new TaskSchedulerAwaitable(scheduler, alwaysYield);
    }

    /// <summary>
    /// Gets an awaitable that schedules the continuation with a preference to executing synchronously on the callstack that completed the <see cref="Task"/>,
    /// without regard to thread ID or any <see cref="SynchronizationContext"/> that may be applied when the continuation is scheduled or when the antecedent completes.
    /// </summary>
    /// <param name="antecedent">The task to await on.</param>
    /// <returns>An awaitable.</returns>
    /// <remarks>
    /// If there is not enough stack space remaining on the thread that is completing the <paramref name="antecedent"/> <see cref="Task"/>,
    /// the continuation may be scheduled on the threadpool.
    /// </remarks>
    public static ExecuteContinuationSynchronouslyAwaitable ConfigureAwaitRunInline(this Task antecedent)
    {
        Requires.NotNull(antecedent, nameof(antecedent));

        return new ExecuteContinuationSynchronouslyAwaitable(antecedent);
    }

    /// <summary>
    /// Gets an awaitable that schedules the continuation with a preference to executing synchronously on the callstack that completed the <see cref="Task"/>,
    /// without regard to thread ID or any <see cref="SynchronizationContext"/> that may be applied when the continuation is scheduled or when the antecedent completes.
    /// </summary>
    /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
    /// <param name="antecedent">The task to await on.</param>
    /// <returns>An awaitable.</returns>
    /// <remarks>
    /// If there is not enough stack space remaining on the thread that is completing the <paramref name="antecedent"/> <see cref="Task"/>,
    /// the continuation may be scheduled on the threadpool.
    /// </remarks>
    public static ExecuteContinuationSynchronouslyAwaitable<T> ConfigureAwaitRunInline<T>(this Task<T> antecedent)
    {
        Requires.NotNull(antecedent, nameof(antecedent));

        return new ExecuteContinuationSynchronouslyAwaitable<T>(antecedent);
    }

    /// <summary>
    /// An awaitable that executes continuations on the specified task scheduler.
    /// </summary>
    public readonly struct TaskSchedulerAwaitable
    {
        /// <summary>
        /// The scheduler for continuations.
        /// </summary>
        private readonly TaskScheduler taskScheduler;

        /// <summary>
        /// A value indicating whether the awaitable will always call the caller to yield.
        /// </summary>
        private readonly bool alwaysYield;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskSchedulerAwaitable"/> struct.
        /// </summary>
        /// <param name="taskScheduler">The task scheduler used to execute continuations.</param>
        /// <param name="alwaysYield">A value indicating whether the caller should yield even if
        /// already executing on the desired task scheduler.</param>
        public TaskSchedulerAwaitable(TaskScheduler taskScheduler, bool alwaysYield = false)
        {
            Requires.NotNull(taskScheduler, nameof(taskScheduler));

            this.taskScheduler = taskScheduler;
            this.alwaysYield = alwaysYield;
        }

        /// <summary>
        /// Gets an awaitable that schedules continuations on the specified scheduler.
        /// </summary>
        public TaskSchedulerAwaiter GetAwaiter()
        {
            return new TaskSchedulerAwaiter(this.taskScheduler, this.alwaysYield);
        }
    }

    /// <summary>
    /// An awaiter returned from <see cref="GetAwaiter(TaskScheduler)"/>.
    /// </summary>
    public readonly struct TaskSchedulerAwaiter : ICriticalNotifyCompletion
    {
        /// <summary>
        /// The scheduler for continuations.
        /// </summary>
        private readonly TaskScheduler scheduler;

        /// <summary>
        /// A value indicating whether <see cref="IsCompleted"/>
        /// should always return false.
        /// </summary>
        private readonly bool alwaysYield;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskSchedulerAwaiter"/> struct.
        /// </summary>
        /// <param name="scheduler">The scheduler for continuations.</param>
        /// <param name="alwaysYield">A value indicating whether the caller should yield even if
        /// already executing on the desired task scheduler.</param>
        public TaskSchedulerAwaiter(TaskScheduler scheduler, bool alwaysYield = false)
        {
            this.scheduler = scheduler;
            this.alwaysYield = alwaysYield;
        }

        /// <summary>
        /// Gets a value indicating whether no yield is necessary.
        /// </summary>
        /// <value><see langword="true" /> if the caller is already running on that TaskScheduler.</value>
        public bool IsCompleted
        {
            get
            {
                if (this.alwaysYield)
                {
                    return false;
                }

                // We special case the TaskScheduler.Default since that is semantically equivalent to being
                // on a ThreadPool thread, and there are various ways to get on those threads.
                // TaskScheduler.Current is never null.  Even if no scheduler is really active and the current
                // thread is not a threadpool thread, TaskScheduler.Current == TaskScheduler.Default, so we have
                // to protect against that case too.
                bool isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                return (this.scheduler == TaskScheduler.Default && isThreadPoolThread)
                    || (this.scheduler == TaskScheduler.Current && TaskScheduler.Current != TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Schedules a continuation to execute using the specified task scheduler.
        /// </summary>
        /// <param name="continuation">The delegate to invoke.</param>
        public void OnCompleted(Action continuation)
        {
            if (this.scheduler == TaskScheduler.Default)
            {
                ThreadPool.QueueUserWorkItem(state => ((Action)state!)(), continuation);
            }
            else
            {
                Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
            }
        }

        /// <summary>
        /// Schedules a continuation to execute using the specified task scheduler
        /// without capturing the ExecutionContext.
        /// </summary>
        /// <param name="continuation">The action.</param>
        public void UnsafeOnCompleted(Action continuation)
        {
            if (this.scheduler == TaskScheduler.Default)
            {
                ThreadPool.UnsafeQueueUserWorkItem(state => ((Action)state!)(), continuation);
            }
            else
            {
                Task.Factory.StartNew(continuation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
            }
        }

        /// <summary>
        /// Does nothing.
        /// </summary>
        public void GetResult()
        {
        }
    }

    /// <summary>
    /// A Task awaitable that has affinity to executing callbacks synchronously on the completing callstack.
    /// </summary>
    public readonly struct ExecuteContinuationSynchronouslyAwaitable
    {
        /// <summary>
        /// The task whose completion will execute the continuation.
        /// </summary>
        private readonly Task antecedent;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaitable"/> struct.
        /// </summary>
        /// <param name="antecedent">The task whose completion will execute the continuation.</param>
        public ExecuteContinuationSynchronouslyAwaitable(Task antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            this.antecedent = antecedent;
        }

        /// <summary>
        /// Gets the awaiter.
        /// </summary>
        /// <returns>The awaiter.</returns>
        public ExecuteContinuationSynchronouslyAwaiter GetAwaiter() => new ExecuteContinuationSynchronouslyAwaiter(this.antecedent);
    }

    /// <summary>
    /// A Task awaiter that has affinity to executing callbacks synchronously on the completing callstack.
    /// </summary>
    public readonly struct ExecuteContinuationSynchronouslyAwaiter : INotifyCompletion
    {
        /// <summary>
        /// The task whose completion will execute the continuation.
        /// </summary>
        private readonly Task antecedent;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaiter"/> struct.
        /// </summary>
        /// <param name="antecedent">The task whose completion will execute the continuation.</param>
        public ExecuteContinuationSynchronouslyAwaiter(Task antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            this.antecedent = antecedent;
        }

        /// <summary>
        /// Gets a value indicating whether the antedent <see cref="Task"/> has already completed.
        /// </summary>
        public bool IsCompleted => this.antecedent.IsCompleted;

        /// <summary>
        /// Rethrows any exception thrown by the antecedent.
        /// </summary>
        public void GetResult() => this.antecedent.GetAwaiter().GetResult();

        /// <summary>
        /// Schedules a callback to run when the antecedent task completes.
        /// </summary>
        /// <param name="continuation">The callback to invoke.</param>
        public void OnCompleted(Action continuation)
        {
            Requires.NotNull(continuation, nameof(continuation));

            this.antecedent.ContinueWith(
                (_, s) => ((Action)s!)(),
                continuation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// A Task awaitable that has affinity to executing callbacks synchronously on the completing callstack.
    /// </summary>
    /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
    public readonly struct ExecuteContinuationSynchronouslyAwaitable<T>
    {
        /// <summary>
        /// The task whose completion will execute the continuation.
        /// </summary>
        private readonly Task<T> antecedent;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaitable{T}"/> struct.
        /// </summary>
        /// <param name="antecedent">The task whose completion will execute the continuation.</param>
        public ExecuteContinuationSynchronouslyAwaitable(Task<T> antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            this.antecedent = antecedent;
        }

        /// <summary>
        /// Gets the awaiter.
        /// </summary>
        /// <returns>The awaiter.</returns>
        public ExecuteContinuationSynchronouslyAwaiter<T> GetAwaiter() => new ExecuteContinuationSynchronouslyAwaiter<T>(this.antecedent);
    }

    /// <summary>
    /// A Task awaiter that has affinity to executing callbacks synchronously on the completing callstack.
    /// </summary>
    /// <typeparam name="T">The type of value returned by the awaited <see cref="Task"/>.</typeparam>
    public readonly struct ExecuteContinuationSynchronouslyAwaiter<T> : INotifyCompletion
    {
        /// <summary>
        /// The task whose completion will execute the continuation.
        /// </summary>
        private readonly Task<T> antecedent;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecuteContinuationSynchronouslyAwaiter{T}"/> struct.
        /// </summary>
        /// <param name="antecedent">The task whose completion will execute the continuation.</param>
        public ExecuteContinuationSynchronouslyAwaiter(Task<T> antecedent)
        {
            Requires.NotNull(antecedent, nameof(antecedent));
            this.antecedent = antecedent;
        }

        /// <summary>
        /// Gets a value indicating whether the antedent <see cref="Task"/> has already completed.
        /// </summary>
        public bool IsCompleted => this.antecedent.IsCompleted;

        /// <summary>
        /// Rethrows any exception thrown by the antecedent.
        /// </summary>
        public T GetResult() => this.antecedent.GetAwaiter().GetResult();

        /// <summary>
        /// Schedules a callback to run when the antecedent task completes.
        /// </summary>
        /// <param name="continuation">The callback to invoke.</param>
        public void OnCompleted(Action continuation)
        {
            Requires.NotNull(continuation, nameof(continuation));

            this.antecedent.ContinueWith(
                (_, s) => ((Action)s!)(),
                continuation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
