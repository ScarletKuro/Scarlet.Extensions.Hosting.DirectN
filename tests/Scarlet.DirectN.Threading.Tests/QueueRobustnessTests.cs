using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Scarlet.DirectN.Threading.Tests;

public class QueueRobustnessTests
{

    [Fact]
    public void PostingThroughTheContextNeverThrowsOnceTheQueueIsClosed()
    {
        var loop = new MessageLoopThread();
        loop.Dispose();

        loop.Queue.SynchronizationContext.Post(_ => { }, null);

        Assert.False(loop.Queue.TryPost(_ => { }, null));
        Assert.Throws<InvalidOperationException>(() => loop.Queue.Post(_ => { }, null));
    }

    [Fact]
    public async Task AnAwaitResumingAfterTheQueueClosedIsDroppedWithoutCrashingTheProcess()
    {
        var loop = new MessageLoopThread();
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = false;
        await loop.InvokeAsync(() =>
        {
            // Started on the UI thread: resumes through its SynchronizationContext.
            _ = AwaitOnUiThread();
            return true;
        });
        loop.Dispose();

        // The runtime posts the continuation to the closed queue; a throwing Post would terminate the test process.
        operation.SetResult();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(resumed);

        async Task AwaitOnUiThread()
        {
            await operation.Task;
            resumed = true;
        }
    }

    [Fact]
    public async Task ASendWhoseCallbackClosesTheQueueBeforeTheSenderWaitsStillSucceeds()
    {
        using var loop = new MessageLoopThread();
        var queue = loop.Queue;

        // Send's two steps, with the whole callback (which closes the queue) in between: the sender only starts waiting after.
        var work = new DirectNSynchronizationContext.SendWork(_ => queue.Close(), null);
        Assert.True(queue.TryPostAbandonable(static s => ((DirectNSynchronizationContext.SendWork)s!).Run(), work, static s => ((DirectNSynchronizationContext.SendWork)s!).Abandon()));
        Assert.True(work.Done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(queue.IsClosed);

        var wait = Task.Run(() => queue.WaitForSend(work), TestContext.Current.CancellationToken);
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        work.ThrowIfFailed();
    }

    [Theory]
    [InlineData("Enqueued")]
    [InlineData("Waking")]
    public async Task WorkThatRanIsNeverReportedAsRejectedAlsoWhenItClosedTheQueueWhilePosting(string stageName)
    {
        var stage = Enum.Parse<DirectNMessageQueue.PostStage>(stageName);
        using var loop = new MessageLoopThread();
        var queue = loop.Queue;
        using var gate = new ManualResetEventSlim();
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Post(_ =>
        {
            draining.SetResult();
            gate.Wait();
        }, null);
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The owner thread is draining its queue. Let it run the new item, which closes the queue (destroying its window),
        // while the poster is still between queuing it and waking the thread.
        queue.TestHook = reached =>
        {
            if (reached == stage)
            {
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => queue.IsClosed, TimeSpan.FromSeconds(10)));
            }
        };
        var ran = false;
        var posted = queue.TryPost(_ =>
        {
            ran = true;
            queue.Close();
        }, null);
        queue.TestHook = null;

        Assert.True(ran);
        Assert.True(posted, "TryPost reported work that ran as rejected.");
    }

    [Fact]
    public async Task WorkDroppedByClosingIsCounted()
    {
        using var loop = new MessageLoopThread();

        var dropped = await loop.InvokeAsync(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                loop.Queue.Post(_ => { }, null);
            }

            loop.Queue.Close();
            return loop.Queue.DroppedCount;
        });

        Assert.Equal(3, dropped);
    }

    [Fact]
    public async Task ABacklogOfSlowerCallbacksStillLetsTimersRunEveryFewMilliseconds()
    {
        using var loop = new MessageLoopThread();
        var timer = await loop.InvokeAsync(() => ThreadTimer.Start(10));
        Assert.NotEqual(0u, (uint)timer);
        try
        {
            // 2 ms each: a whole wake-up's worth of items (256) would keep the timer waiting for half a second.
            const int Items = 600;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ThreadTimer.ResetLongestGap();
            for (var i = 0; i < Items; i++)
            {
                loop.Queue.Post(static _ => Spin(TimeSpan.FromMilliseconds(2)), null);
            }

            loop.Queue.Post(_ => done.SetResult(), null);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var gap = ThreadTimer.LongestGap;

            // A time budget per wake-up (about a frame) plus the timer's own granularity (about 16 ms), with room for a busy machine.
            Assert.True(gap < TimeSpan.FromMilliseconds(150), $"The timer waited up to {gap.TotalMilliseconds:F0} ms during the backlog.");
        }
        finally
        {
            await loop.InvokeAsync(() => ThreadTimer.Stop(timer));
        }
    }

    [Fact]
    public async Task ABacklogOfPostsDoesntStarveTimers()
    {
        using var loop = new MessageLoopThread();
        var timer = await loop.InvokeAsync(() => ThreadTimer.Start(10));
        Assert.NotEqual(0u, (uint)timer);
        try
        {
            // About a second of work, posted at once (and some more while it runs, as other threads would).
            const int Items = 20_000;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            for (var i = 0; i < Items; i++)
            {
                loop.Queue.Post(static _ => Spin(TimeSpan.FromTicks(500)), null);
            }

            loop.Queue.Post(_ => done.SetResult(), null);
            var ticksBefore = ThreadTimer.Ticks;
            var poster = Task.Run(
                () =>
                {
                    while (!done.Task.IsCompleted)
                    {
                        loop.Queue.TryPost(static _ => { }, null);
                        Spin(TimeSpan.FromMilliseconds(0.1));
                    }
                },
                TestContext.Current.CancellationToken);
            var watch = Stopwatch.StartNew();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var ticks = ThreadTimer.Ticks - ticksBefore;
            await poster;

            // A 10 ms timer, while the backlog took this long; posted wake-ups alone would give it none.
            Assert.True(ticks >= 5, $"{ticks} timer ticks in {watch.ElapsedMilliseconds} ms of backlog.");
        }
        finally
        {
            await loop.InvokeAsync(() => ThreadTimer.Stop(timer));
        }
    }

    private static void Spin(TimeSpan duration)
    {
        var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
        }
    }
}

/// <summary>
/// A Win32 thread timer of the calling thread, counting its ticks.
/// </summary>
internal static unsafe partial class ThreadTimer
{
    private static int s_ticks;
    private static long s_lastTick;
    private static long s_maxGap;

    public static int Ticks => Volatile.Read(ref s_ticks);

    /// <summary>
    /// Starts measuring the longest time without a tick, from now.
    /// </summary>
    public static void ResetLongestGap()
    {
        Volatile.Write(ref s_maxGap, 0);
        Volatile.Write(ref s_lastTick, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Gets the longest time without a tick since <see cref="ResetLongestGap"/>, up to now.
    /// </summary>
    public static TimeSpan LongestGap =>
        Stopwatch.GetElapsedTime(0, Math.Max(Volatile.Read(ref s_maxGap), Stopwatch.GetTimestamp() - Volatile.Read(ref s_lastTick)));

    public static nuint Start(uint milliseconds) => SetTimer(0, 0, milliseconds, &OnTick);

    public static bool Stop(nuint timer) => KillTimer(0, timer);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnTick(nint hwnd, uint msg, nuint id, uint time)
    {
        Interlocked.Increment(ref s_ticks);
        var now = Stopwatch.GetTimestamp();
        var gap = now - Interlocked.Exchange(ref s_lastTick, now);
        if (gap > Volatile.Read(ref s_maxGap))
        {
            Volatile.Write(ref s_maxGap, gap);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, delegate* unmanaged[Stdcall]<nint, uint, nuint, uint, void> timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hwnd, nuint id);
}
