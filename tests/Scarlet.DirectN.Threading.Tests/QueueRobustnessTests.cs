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
        Assert.True(queue.TryPost(static s => ((DirectNSynchronizationContext.SendWork)s!).Run(), work));
        Assert.True(work.Done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(queue.IsClosed);

        var wait = Task.Run(() => queue.WaitForSend(work), TestContext.Current.CancellationToken);
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        work.ThrowIfFailed();
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

    public static int Ticks => Volatile.Read(ref s_ticks);

    public static nuint Start(uint milliseconds) => SetTimer(0, 0, milliseconds, &OnTick);

    public static bool Stop(nuint timer) => KillTimer(0, timer);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnTick(nint hwnd, uint msg, nuint id, uint time) => Interlocked.Increment(ref s_ticks);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, delegate* unmanaged[Stdcall]<nint, uint, nuint, uint, void> timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hwnd, nuint id);
}
