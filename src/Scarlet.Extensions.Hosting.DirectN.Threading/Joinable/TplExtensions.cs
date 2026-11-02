// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// Extensions to the Task Parallel Library.
/// </summary>
internal static partial class TplExtensions
{
    /// <summary>
    /// Applies one task's results to another.
    /// </summary>
    /// <typeparam name="T">The type of value returned by a task.</typeparam>
    /// <param name="task">The task whose completion should be applied to another.</param>
    /// <param name="tcs">The task that should receive the completion status.</param>
    public static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs)
    {
        ApplyResultTo(task, tcs, inlineSubsequentCompletion: true);
    }

    /// <summary>
    /// Applies one task's results to another.
    /// </summary>
    /// <typeparam name="T">The type of value returned by a task.</typeparam>
    /// <param name="task">The task whose completion should be applied to another.</param>
    /// <param name="tcs">The task that should receive the completion status.</param>
    public static void ApplyResultTo<T>(this Task task, TaskCompletionSource<T> tcs)
    //// where T : defaultable
    {
        Requires.NotNull(task, nameof(task));
        Requires.NotNull(tcs, nameof(tcs));

        if (task.IsCompleted)
        {
            ApplyCompletedTaskResultTo<T>(task, tcs, default(T)!);
        }
        else
        {
            // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
            task.ContinueWith(
                (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s!, default(T)!),
                tcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
    /// </summary>
    /// <param name="task">The task whose result is to be ignored.</param>
    public static void Forget(this Task? task)
    {
    }

    /// <summary>
    /// Applies one task's results to another.
    /// </summary>
    /// <typeparam name="T">The type of value returned by a task.</typeparam>
    /// <param name="task">The task whose completion should be applied to another.</param>
    /// <param name="tcs">The task that should receive the completion status.</param>
    /// <param name="inlineSubsequentCompletion">
    /// <see langword="true" /> to complete the supplied <paramref name="tcs"/> as efficiently as possible (inline with the completion of <paramref name="task"/>);
    /// <see langword="false" /> to complete the <paramref name="tcs"/> asynchronously.
    /// Note if <paramref name="task"/> is completed when this method is invoked, then <paramref name="tcs"/> is always completed synchronously.
    /// </param>
    internal static void ApplyResultTo<T>(this Task<T> task, TaskCompletionSource<T> tcs, bool inlineSubsequentCompletion)
    {
        Requires.NotNull(task, nameof(task));
        Requires.NotNull(tcs, nameof(tcs));

        if (task.IsCompleted)
        {
            ApplyCompletedTaskResultTo(task, tcs);
        }
        else
        {
            // Using a minimum of allocations (just one task, and no closure) ensure that one task's completion sets equivalent completion on another task.
            task.ContinueWith(
                (t, s) => ApplyCompletedTaskResultTo(t, (TaskCompletionSource<T>)s!),
                tcs,
                CancellationToken.None,
                inlineSubsequentCompletion ? TaskContinuationOptions.ExecuteSynchronously : TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Returns a reusable task that is already canceled.
    /// </summary>
    /// <typeparam name="T">The type parameter for the returned task.</typeparam>
    internal static Task<T> CanceledTaskOfT<T>() => CanceledTaskOfTCache<T>.CanceledTask;

    /// <summary>
    /// Applies a completed task's results to another.
    /// </summary>
    /// <typeparam name="T">The type of value returned by a task.</typeparam>
    /// <param name="completedTask">The task whose completion should be applied to another.</param>
    /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
    private static void ApplyCompletedTaskResultTo<T>(Task<T> completedTask, TaskCompletionSource<T> taskCompletionSource)
    {
        Assumes.NotNull(completedTask);
        Assumes.True(completedTask.IsCompleted);
        Assumes.NotNull(taskCompletionSource);

        if (completedTask.IsCanceled)
        {
            // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
            // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
            // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
            taskCompletionSource.TrySetCanceled();
        }
        else if (completedTask.IsFaulted)
        {
            taskCompletionSource.TrySetException(completedTask.Exception!.InnerExceptions);
        }
        else
        {
            taskCompletionSource.TrySetResult(completedTask.Result);
        }
    }

    /// <summary>
    /// Applies a completed task's results to another.
    /// </summary>
    /// <typeparam name="T">The type of value returned by a task.</typeparam>
    /// <param name="completedTask">The task whose completion should be applied to another.</param>
    /// <param name="taskCompletionSource">The task that should receive the completion status.</param>
    /// <param name="valueOnRanToCompletion">The value to set on the completion source when the source task runs to completion.</param>
    private static void ApplyCompletedTaskResultTo<T>(Task completedTask, TaskCompletionSource<T> taskCompletionSource, T valueOnRanToCompletion)
    {
        Assumes.NotNull(completedTask);
        Assumes.True(completedTask.IsCompleted);
        Assumes.NotNull(taskCompletionSource);

        if (completedTask.IsCanceled)
        {
            // NOTE: this is "lossy" in that we don't propagate any CancellationToken that the Task would throw an OperationCanceledException with.
            // Propagating that data would require that we actually cause the completedTask to throw so we can inspect the
            // OperationCanceledException.CancellationToken property, which we consider more costly than it's worth.
            taskCompletionSource.TrySetCanceled();
        }
        else if (completedTask.IsFaulted)
        {
            taskCompletionSource.TrySetException(completedTask.Exception!.InnerExceptions);
        }
        else
        {
            taskCompletionSource.TrySetResult(valueOnRanToCompletion);
        }
    }

    /// <summary>
    /// A cache for canceled <see cref="Task{T}"/> instances.
    /// </summary>
    /// <typeparam name="T">The type parameter for the returned task.</typeparam>
    private static class CanceledTaskOfTCache<T>
    {
        /// <summary>
        /// A task that is already canceled.
        /// </summary>
        internal static readonly Task<T> CanceledTask = Task.FromCanceled<T>(new CancellationToken(canceled: true));
    }
}
