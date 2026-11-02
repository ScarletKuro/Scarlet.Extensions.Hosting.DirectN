using System.Collections.Concurrent;

namespace Scarlet.DirectN.Threading.Tests;

public class DirectNMessageQueueTests
{
    [Fact]
    public async Task PostedWorkRunsInOrderOnTheOwningThreadWithItsContextInstalled()
    {
        using var loop = new MessageLoopThread();
        var results = new ConcurrentQueue<(int Index, bool OnThread, bool ContextInstalled)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // More than one wake-up's worth (256), so the queue has to wake itself up again.
        const int Count = 1000;
        for (var i = 0; i < Count; i++)
        {
            var index = i;
            loop.Queue.Post(_ =>
            {
                results.Enqueue((index, Thread.CurrentThread == loop.Thread, SynchronizationContext.Current == loop.Queue.SynchronizationContext));
                if (index == Count - 1)
                {
                    done.SetResult();
                }
            }, null);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, Count), results.Select(r => r.Index));
        Assert.All(results, r => Assert.True(r.OnThread && r.ContextInstalled));
    }

    [Fact]
    public async Task OneQueuePerThread()
    {
        using var loop = new MessageLoopThread();

        var (same, current) = await loop.InvokeAsync(() => (DirectNMessageQueue.GetOrCreateForCurrentThread(), DirectNMessageQueue.Current));

        Assert.Same(loop.Queue, same);
        Assert.Same(loop.Queue, current);
        Assert.Same(loop.Thread, loop.Queue.Thread);
        Assert.Same(loop.Queue, loop.Queue.SynchronizationContext.Queue);
    }

    [Fact]
    public async Task CheckAccessIsOnlyTrueOnTheOwningThread()
    {
        using var loop = new MessageLoopThread();

        Assert.False(loop.Queue.CheckAccess());
        Assert.True(await loop.InvokeAsync(loop.Queue.CheckAccess));
    }

    [Fact]
    public async Task AwaitResumesOnTheOwningThread()
    {
        using var loop = new MessageLoopThread();

        var onThread = await loop.InvokeAsync(async () =>
        {
            await Task.Delay(10);
            return Thread.CurrentThread == loop.Thread;
        }).Unwrap();

        Assert.True(onThread);
    }

    [Fact]
    public async Task SendRunsInlineOnTheOwningThreadAndWaitsFromOtherThreads()
    {
        using var loop = new MessageLoopThread();
        var context = loop.Queue.SynchronizationContext;

        Thread? ranOn = null;
        context.Send(_ => ranOn = Thread.CurrentThread, null);
        Assert.Same(loop.Thread, ranOn);

        var inline = await loop.InvokeAsync(() =>
        {
            var ran = false;
            context.Send(_ => ran = true, null);
            return ran;
        });
        Assert.True(inline);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Send(_ => throw new InvalidOperationException("sent"), null));
        Assert.Equal("sent", exception.Message);
    }

    [Fact]
    public async Task HandledUnhandledExceptionsKeepTheQueueRunning()
    {
        using var loop = new MessageLoopThread();
        var reported = new TaskCompletionSource<(string Message, bool OnThread)>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Queue.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            reported.TrySetResult((e.Exception.Message, Thread.CurrentThread == loop.Thread));
        };

        loop.Queue.Post(_ => throw new InvalidOperationException("posted"), null);

        var (message, onThread) = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("posted", message);
        Assert.True(onThread);
        Assert.Equal(42, await loop.InvokeAsync(() => 42));
    }

    [Fact]
    public void TryPostReturnsFalseOnceTheThreadHasExited()
    {
        var loop = new MessageLoopThread();
        loop.Dispose();
        Assert.False(loop.Thread.IsAlive);

        Assert.False(loop.Queue.TryPost(_ => { }, null));
        Assert.Throws<System.ComponentModel.Win32Exception>(() => loop.Queue.Post(_ => { }, null));
    }

    [Fact]
    public void CreateCopyReturnsTheSameContext()
    {
        using var loop = new MessageLoopThread();

        Assert.Same(loop.Queue.SynchronizationContext, loop.Queue.SynchronizationContext.CreateCopy());
    }
}
