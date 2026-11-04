namespace Scarlet.DirectN.Threading.Tests;

public class QueueLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CloseReleasesOwnerStateAfterWindowLossAlreadyClosedTheQueue()
    {
        using var loop = new MessageLoopThread();
        await loop.InvokeAsync(() =>
        {
            var queue = loop.Queue;
            Assert.True(global::DirectN.Functions.DestroyWindow(queue.WindowHandleForTests));
            Assert.False(queue.TryPost(() => Assert.Fail("Work ran after window loss")));
            Assert.True(queue.IsClosed);
            Assert.Equal(QueueCloseReason.WindowGone, queue.CloseReason);
            queue.Close();
            Assert.Null(DirectNMessageQueue.Current);
            queue.Close();
            var replacement = DirectNMessageQueue.GetOrCreateForCurrentThread();
            try
            {
                Assert.NotSame(queue, replacement);
                Assert.False(replacement.IsClosed);
            }
            finally
            {
                replacement.Close();
            }

            return true;
        }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DestroyingTheWindowAbandonsQueuedInvocationsWithoutAnotherPost()
    {
        using var loop = new MessageLoopThread();
        var pending = await loop.InvokeAsync(() =>
        {
            var task = loop.Queue.InvokeAsync(() => Assert.Fail("Work ran after its window was destroyed"),
                TestContext.Current.CancellationToken);
            Assert.True(global::DirectN.Functions.DestroyWindow(loop.Queue.WindowHandleForTests));
            Assert.True(loop.Queue.IsClosed);
            loop.Queue.Close();
            return task;
        }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("OwnerHandleOpened")]
    [InlineData("WindowCreated")]
    [InlineData("UserDataAttached")]
    [InlineData("ExitMonitorRegistered")]
    public async Task ConstructionFailureDestroysItsWindowAndAllowsRetry(string stageName)
    {
        using var loop = new MessageLoopThread();
        var failureStage = Enum.Parse<DirectNMessageQueue.ConstructionStage>(stageName);
        await loop.InvokeAsync(() =>
        {
            loop.Queue.Close();
            var error = new InvalidOperationException("injected initialization failure");
            global::DirectN.HWND createdWindow = default;
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                DirectNMessageQueue.GetOrCreateForCurrentThreadForTests((stage, window) =>
                {
                    if (window.Value != 0)
                    {
                        createdWindow = window;
                    }

                    if (stage == failureStage)
                    {
                        throw error;
                    }
                }));
            Assert.Same(error, thrown);
            Assert.Null(DirectNMessageQueue.Current);
            if (createdWindow.Value != 0)
            {
                Assert.False(global::DirectN.Functions.IsWindow(createdWindow));
            }

            var replacement = DirectNMessageQueue.GetOrCreateForCurrentThread();
            try
            {
                Assert.False(replacement.IsClosed);
            }
            finally
            {
                replacement.Close();
            }

            return true;
        }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }
}
