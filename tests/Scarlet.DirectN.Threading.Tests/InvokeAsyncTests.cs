#pragma warning disable xUnit1051 // Exercise overloads with the default cancellation token; every asynchronous wait is bounded.
namespace Scarlet.DirectN.Threading.Tests;

public class InvokeAsyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AllOverloadsRunOnOwnerAndAsyncCallbacksAreUnwrapped()
    {
        using var loop = new MessageLoopThread();
        var queue = loop.Queue;
        await queue.InvokeAsync(() => queue.VerifyAccess()).WaitAsync(Timeout);
        Assert.Equal(loop.Thread, await queue.InvokeAsync(() => Thread.CurrentThread).WaitAsync(Timeout));
        await queue.InvokeAsync(async () =>
        {
            queue.VerifyAccess();
            await Task.Yield();
            queue.VerifyAccess();
        }).WaitAsync(Timeout);
        Assert.Equal(42, await queue.InvokeAsync(async () =>
        {
            await Task.Yield();
            queue.VerifyAccess();
            return 42;
        }).WaitAsync(Timeout));
        Assert.Throws<InvalidOperationException>(queue.VerifyAccess);
    }

    [Fact]
    public async Task InvocationsAlwaysEnqueueInOrderEvenOnOwner()
    {
        using var loop = new MessageLoopThread();
        var order = new List<int>();
        var tasks = await loop.InvokeAsync(() =>
        {
            loop.Queue.Post(_ => order.Add(1), null);
            var first = loop.Queue.InvokeAsync(() => { order.Add(2); });
            var second = loop.Queue.InvokeAsync(() => { order.Add(3); });
            Assert.False(first.IsCompleted);
            Assert.Empty(order);
            return Task.WhenAll(first, second);
        });
        await tasks.WaitAsync(Timeout);
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task ExceptionsAreReturnedIncludingAfterAnAwait()
    {
        using var loop = new MessageLoopThread();
        var error = new FormatException("callback");
        var sync = loop.Queue.InvokeAsync((Action)(() => throw error));
        Assert.Same(error, await Assert.ThrowsAsync<FormatException>(() => sync).WaitAsync(Timeout));
        var syncResult = loop.Queue.InvokeAsync((Func<int>)(() => throw error));
        Assert.Same(error, await Assert.ThrowsAsync<FormatException>(() => syncResult).WaitAsync(Timeout));
        var asyncTask = loop.Queue.InvokeAsync(async () =>
        {
            await Task.Yield();
            throw error;
        });
        Assert.Same(error, await Assert.ThrowsAsync<FormatException>(() => asyncTask).WaitAsync(Timeout));
        Assert.True(await loop.Queue.InvokeAsync(() => true).WaitAsync(Timeout));
    }

    [Fact]
    public async Task CancellationBeforeDispatchSkipsCallbackAndPreservesToken()
    {
        using var loop = new MessageLoopThread();
        using var cancel = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Queue.Post(_ => { entered.SetResult(); gate.Wait(Timeout); }, null);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            var ran = false;
            var task = loop.Queue.InvokeAsync(() => { ran = true; }, cancel.Token);
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
            Assert.Equal(cancel.Token, error.CancellationToken);
            var alreadyCanceled = loop.Queue.InvokeAsync(() => { ran = true; }, cancel.Token);
            Assert.True(alreadyCanceled.IsCanceled);
            gate.Set();
            await loop.Queue.InvokeAsync(() => { }).WaitAsync(Timeout);
            Assert.False(ran);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task CancellationAfterStartDoesNotCompleteTheInvocationEarly()
    {
        using var loop = new MessageLoopThread();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = loop.Queue.InvokeAsync(async () =>
        {
            entered.SetResult();
            return await finish.Task;
        }, cancel.Token);
        await entered.Task.WaitAsync(Timeout);
        cancel.Cancel();
        Assert.False(task.IsCompleted);
        finish.SetResult(42);
        Assert.Equal(42, await task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task CallbackCancellationIsPropagated()
    {
        using var loop = new MessageLoopThread();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var task = loop.Queue.InvokeAsync(() => Task.FromCanceled<int>(cancel.Token));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
        Assert.True(task.IsCanceled);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Fact]
    public async Task SynchronousCallbackCancellationIsPropagated()
    {
        using var loop = new MessageLoopThread();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var task = loop.Queue.InvokeAsync((Func<int>)(() => throw new OperationCanceledException(cancel.Token)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
        Assert.True(task.IsCanceled);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Fact]
    public async Task CloseAbandonsAcceptedInvocationsAndRejectsNewOnes()
    {
        using var loop = new MessageLoopThread();
        using var gate = new ManualResetEventSlim();
        loop.Queue.Post(_ => { gate.Wait(Timeout); loop.Queue.Close(); }, null);
        var task = loop.Queue.InvokeAsync(() => Assert.Fail("Abandoned work ran"));
        gate.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task).WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Queue.InvokeAsync(() => { })).WaitAsync(Timeout);
    }

    [Fact]
    public async Task UnexpectedOwnerExitAbandonsAcceptedInvocationsWithoutAnotherPost()
    {
        using var loop = new MessageLoopThread();
        using var gate = new ManualResetEventSlim();
        var paused = loop.PauseAfterCurrentMessage(gate);
        loop.Queue.Post(_ => { }, null);
        await paused.WaitAsync(Timeout);
        loop.StopWithoutDraining();
        var task = loop.Queue.InvokeAsync(() => Assert.Fail("Work ran after loop exit"));
        gate.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task).WaitAsync(Timeout);
    }

    [Fact]
    public async Task ExecutionContextFlowsAndContinuationsDoNotRunInline()
    {
        using var loop = new MessageLoopThread();
        var local = new AsyncLocal<string?> { Value = "caller" };
        using var gate = new ManualResetEventSlim();
        loop.Queue.Post(_ => gate.Wait(Timeout), null);
        var task = loop.Queue.InvokeAsync(() =>
        {
            Assert.Equal("caller", local.Value);
            local.Value = "callback";
            return Thread.CurrentThread;
        });
        var continuation = task.ContinueWith(_ => Thread.CurrentThread,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        gate.Set();
        Assert.Same(loop.Thread, await task.WaitAsync(Timeout));
        Assert.NotSame(loop.Thread, await continuation.WaitAsync(Timeout));
        Assert.Equal("caller", local.Value);
    }

    [Fact]
    public void NullCallbacksThrowSynchronously()
    {
        using var loop = new MessageLoopThread();
        Assert.Throws<ArgumentNullException>(() => { _ = loop.Queue.InvokeAsync((Action)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = loop.Queue.InvokeAsync((Func<int>)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = loop.Queue.InvokeAsync((Func<Task>)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = loop.Queue.InvokeAsync((Func<Task<int>>)null!); });
    }
}
