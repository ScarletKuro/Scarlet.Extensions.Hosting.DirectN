// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Scarlet.Extensions.Hosting.DirectN.Threading.Tests.Joinable;

public abstract class TestBase
{
    protected const int AsyncDelay = 500;

    protected const int TestTimeout = 5000;

    /// <summary>
    /// The maximum length of time to wait for something that we expect will happen
    /// within the timeout.
    /// </summary>
    protected static readonly TimeSpan UnexpectedTimeout = Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum length of time to wait for something that we do not expect will happen
    /// within the timeout.
    /// </summary>
    protected static readonly TimeSpan ExpectedTimeout = TimeSpan.FromSeconds(2);

    protected TestBase(ITestOutputHelper logger)
    {
        this.Logger = logger;
    }

    /// <summary>
    /// Gets or sets the source of <see cref="TimeoutToken"/> that influences
    /// when tests consider themselves to be timed out.
    /// </summary>
    protected CancellationTokenSource TimeoutTokenSource { get; set; } = new CancellationTokenSource(UnexpectedTimeout);

    /// <summary>
    /// Gets a token that is canceled when the test times out,
    /// per the policy set by <see cref="TimeoutTokenSource"/>.
    /// </summary>
    protected CancellationToken TimeoutToken => this.TimeoutTokenSource.Token;

    /// <summary>
    /// Gets or sets the logger to use for writing text to be captured in the test results.
    /// </summary>
    protected ITestOutputHelper Logger { get; set; }

    /// <summary>
    /// Verifies that continuations scheduled on a task will not be executed inline with the specified completing action.
    /// </summary>
    /// <param name="antecedent">The task to test.</param>
    /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
    protected static void VerifyDoesNotInlineContinuations(Task antecedent, Action completingAction)
    {
        Requires.NotNull(antecedent, nameof(antecedent));
        Requires.NotNull(completingAction, nameof(completingAction));

        var completingActionFinished = new ManualResetEventSlim();
        Task? continuation = antecedent.ContinueWith(
            _ => Assert.True(completingActionFinished.Wait(AsyncDelay)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        completingAction();
        completingActionFinished.Set();

        // Rethrow the exception if it turned out it deadlocked.
        continuation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Verifies that continuations scheduled on a task can be executed inline with the specified completing action.
    /// </summary>
    /// <param name="antecedent">The task to test.</param>
    /// <param name="completingAction">The action that results in the synchronous completion of the task.</param>
    protected static void VerifyCanInlineContinuations(Task antecedent, Action completingAction)
    {
        Requires.NotNull(antecedent, nameof(antecedent));
        Requires.NotNull(completingAction, nameof(completingAction));

        Thread callingThread = Thread.CurrentThread;
        Task? continuation = antecedent.ContinueWith(
            _ => Assert.Equal(callingThread, Thread.CurrentThread),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        completingAction();
        Assert.True(continuation.IsCompleted);

        // Rethrow any exceptions.
        continuation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the delegate on a thread with <see cref="ApartmentState.STA"/>
    /// and without a current <see cref="SynchronizationContext"/>.
    /// </summary>
    /// <param name="action">The delegate to execute.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown on non-Windows OS.</exception>
    protected void ExecuteOnSTA(Action action)
    {
        Requires.NotNull(action, nameof(action));

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && SynchronizationContext.Current is null)
        {
            action();
            return;
        }

        Exception? staFailure = null;
        var staThread = new Thread(state =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                staFailure = ex;
            }
        });
#pragma warning disable CA1416 // Validate platform compatibility
        staThread.SetApartmentState(ApartmentState.STA);
#pragma warning restore CA1416 // Validate platform compatibility
        staThread.Start();
        staThread.Join();
        if (staFailure is object)
        {
            ExceptionDispatchInfo.Capture(staFailure).Throw(); // rethrow preserving callstack.
        }
    }

    protected void ExecuteOnDispatcher(Action action)
    {
        this.ExecuteOnDispatcher(delegate
        {
            action();
            return Task.CompletedTask;
        });
    }

    protected void ExecuteOnDispatcher(Func<Task> action)
    {
        if (!SingleThreadedTestSynchronizationContext.IsSingleThreadedSyncContext(SynchronizationContext.Current))
        {
            SynchronizationContext.SetSynchronizationContext(SingleThreadedTestSynchronizationContext.New());
        }

        SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
        Exception? failure = null;
        SynchronizationContext.Current!.Post(
            async _ =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            },
            null);

        SingleThreadedTestSynchronizationContext.PushFrame(SynchronizationContext.Current, frame);
        if (failure is object)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
