namespace Scarlet.Extensions.Hosting.DirectN.Threading.Tests;

/// <summary>
/// JoinableTaskContext relies on NoMessagePumpSyncContext to keep blocking waits inside its locks from pumping messages on the
/// (STA) main thread, which only works if the runtime routes blocking waits through <see cref="SynchronizationContext.Wait"/>.
/// This also runs as a Native AOT executable (xunit.v3.aot), where that routing is runtime-specific.
/// </summary>
public class NoMessagePumpSyncContextTests
{
    [Fact]
    public void BlockingWaitsOnAnStaThreadGoThroughTheContext()
    {
        var context = new CountingNoMessagePumpSyncContext();
        var (waited, signaled) = RunOnStaThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            using var signaledEvent = new ManualResetEvent(true);
            return signaledEvent.WaitOne(TimeSpan.FromSeconds(5));
        });

        Assert.True(signaled);
        Assert.True(waited > 0, "SynchronizationContext.Wait was not called: blocking waits don't honor SetWaitNotificationRequired on this runtime.");
        Assert.Equal(waited, context.WaitCount);
    }

    [Fact]
    public void WaitTimesOutWithoutPumping()
    {
        var (_, signaled) = RunOnStaThread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new CountingNoMessagePumpSyncContext());
            using var neverSet = new ManualResetEvent(false);
            return neverSet.WaitOne(TimeSpan.FromMilliseconds(50));
        });

        Assert.False(signaled);
    }

    private static (int WaitCount, bool Result) RunOnStaThread(Func<bool> body)
    {
        var result = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA body failed.", failure);
        }

        return (CountingNoMessagePumpSyncContext.LastCount, result);
    }

    private sealed class CountingNoMessagePumpSyncContext : NoMessagePumpSyncContext
    {
        public static int LastCount { get; private set; }

        public int WaitCount { get; private set; }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            LastCount = ++WaitCount;
            return base.Wait(waitHandles, waitAll, millisecondsTimeout);
        }
    }
}
