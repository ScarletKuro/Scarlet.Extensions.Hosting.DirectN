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
        Assert.Throws<InvalidOperationException>(() => loop.Queue.Post(_ => { }, null));
        Assert.True(loop.Queue.IsClosed);
    }

    [Fact]
    public async Task PostsAreRejectedWhenTheThreadExitedWithAWakeUpStillPending()
    {
        // The exit races with the window teardown; repeat to make the outcome deterministic, not lucky.
        for (var i = 0; i < 200; i++)
        {
            var loop = new MessageLoopThread();
            await loop.InvokeAsync(() =>
            {
                // Leaves a wake-up message in the thread's queue that nothing will ever process.
                loop.Queue.Post(_ => { }, null);
                loop.StopWithoutDraining();
                return true;
            });
            Assert.True(loop.Thread.Join(TimeSpan.FromSeconds(10)));

            Assert.False(loop.Queue.TryPost(_ => { }, null), $"Iteration {i}: TryPost accepted work for an exited thread.");
            var send = Task.Run(() => loop.Queue.SynchronizationContext.Send(_ => { }, null), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SendIsReleasedWhenTheOwnerExitsWithoutClosing()
    {
        var loop = new MessageLoopThread();
        await loop.InvokeAsync(() =>
        {
            loop.StopWithoutDraining(linger: TimeSpan.FromMilliseconds(500));
            return true;
        });
        await loop.LoopEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Accepted (the thread is still alive), never run (it no longer pumps), then the thread exits.
        var send = Task.Run(() => loop.Queue.SynchronizationContext.Send(_ => { }, null), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(loop.Thread.IsAlive);
    }

    [Fact]
    public async Task PostsAndSendsRacingWithCloseNeverHangOrCrash()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            using var loop = new MessageLoopThread();
            var unexpected = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            loop.Queue.UnhandledException += (_, e) => unexpected.Add(e.Exception);
            var ran = 0;
            var rejected = 0;

            var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                for (var n = 0; ; n++)
                {
                    try
                    {
                        if ((n + worker) % 3 == 0)
                        {
                            loop.Queue.SynchronizationContext.Send(_ => Interlocked.Increment(ref ran), null);
                        }
                        else if (!loop.Queue.TryPost(_ => Interlocked.Increment(ref ran), null))
                        {
                            Interlocked.Increment(ref rejected);
                            return;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref rejected);
                        return;
                    }
#pragma warning disable CA1031 // Collected and asserted below.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        unexpected.Add(ex);
                        return;
                    }
                }
            }, TestContext.Current.CancellationToken)).ToArray();

            await Task.Delay(Random.Shared.Next(1, 20), TestContext.Current.CancellationToken);
            await loop.InvokeAsync(() =>
            {
                loop.Queue.Close();
                return true;
            });

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(unexpected);
            Assert.Equal(4, rejected);
        }
    }

    [Fact]
    public async Task WorkIsDeliveredWhenTheThreadMessageQueueIsTemporarilyFull()
    {
        using var loop = new MessageLoopThread();
        var gate = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Queue.Post(_ =>
        {
            blocked.SetResult();
            gate.Wait(TimeSpan.FromSeconds(30));
        }, null);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Fill the thread's message queue (10,000 messages by default) while it is blocked.
        const uint WM_APP = 0x8000;
        var filled = false;
        for (var i = 0; i < 20_000; i++)
        {
            if (!global::DirectN.Functions.PostThreadMessageW(loop.NativeThreadId, WM_APP, default, default))
            {
                filled = true;
                break;
            }
        }

        Assert.True(filled, "Could not fill the thread's message queue.");

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(loop.Queue.TryPost(_ => ran.SetResult(), null));
        gate.Set();

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void QueueIsCollectableOnceItsThreadHasExited()
    {
        var queue = ExitThreadAndForgetQueue();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(queue.IsAlive);
    }

    [Fact]
    public async Task CloseReleasesTheQueueAndAbortsPendingSends()
    {
        using var loop = new MessageLoopThread();
        var gate = new ManualResetEventSlim();
        loop.Queue.Post(_ => gate.Wait(TimeSpan.FromSeconds(10)), null);
        var currentAfterClose = new TaskCompletionSource<DirectNMessageQueue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Queue.Post(_ =>
        {
            loop.Queue.Close();
            currentAfterClose.SetResult(DirectNMessageQueue.Current);
        }, null);
        var send = Task.Run(() => loop.Queue.SynchronizationContext.Send(_ => { }, null), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(loop.Queue.Close);
        gate.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => send).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(loop.Queue.TryPost(_ => { }, null));
        Assert.Null(await currentAfterClose.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ExitThreadAndForgetQueue()
    {
        var loop = new MessageLoopThread();
        var queue = new WeakReference(loop.Queue);
        loop.Dispose();
        return queue;
    }

    [Fact]
    public void CreateCopyReturnsTheSameContext()
    {
        using var loop = new MessageLoopThread();

        Assert.Same(loop.Queue.SynchronizationContext, loop.Queue.SynchronizationContext.CreateCopy());
    }
}
