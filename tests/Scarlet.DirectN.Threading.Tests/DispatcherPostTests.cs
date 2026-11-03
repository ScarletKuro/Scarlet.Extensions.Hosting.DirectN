using System.ComponentModel;

namespace Scarlet.DirectN.Threading.Tests;

public class DispatcherPostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PostsShareQueueOrderingAndNeverRunInlineOnOwner()
    {
        using var loop = new MessageLoopThread();
        DirectNDispatcher dispatcher = loop.Queue;
        var order = new List<int>();
        await loop.InvokeAsync(() =>
        {
            dispatcher.Post(() =>
            {
                dispatcher.VerifyAccess();
                Assert.Same(loop.Queue.SynchronizationContext, SynchronizationContext.Current);
                order.Add(1);
            });
            Assert.True(dispatcher.TryPost(() => order.Add(2)));
            // Verify the inherited overloads can also be called through the queue type.
            loop.Queue.Post(() => order.Add(3));
            Assert.True(loop.Queue.TryPost(() => order.Add(4)));
            loop.Queue.Post(_ => order.Add(5), null);
            Assert.Empty(order);
            return true;
        }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await dispatcher.InvokeAsync(() => { }, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3, 4, 5], order);
    }

    [Fact]
    public async Task PostedExceptionsUseTheUnhandledExceptionEvent()
    {
        using var loop = new MessageLoopThread();
        DirectNDispatcher dispatcher = loop.Queue;
        var observed = new List<Exception>();
        loop.Queue.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            dispatcher.VerifyAccess();
            observed.Add(args.Exception);
        };
        var first = new FormatException("Post");
        var second = new FormatException("TryPost");
        dispatcher.Post(() => throw first);
        Assert.True(dispatcher.TryPost(() => throw second));
        await dispatcher.InvokeAsync(() => { }, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal([first, second], observed);
    }

    [Fact]
    public async Task PostsCaptureExecutionContextWithoutLeakingCallbackChanges()
    {
        using var loop = new MessageLoopThread();
        DirectNDispatcher dispatcher = loop.Queue;
        var local = new AsyncLocal<string?> { Value = "first" };
        var observed = new List<string?>();
        dispatcher.Post(() =>
        {
            observed.Add(local.Value);
            local.Value = "callback";
        });
        local.Value = "second";
        Assert.True(dispatcher.TryPost(() => observed.Add(local.Value)));
        await dispatcher.InvokeAsync(() => { }, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(["first", "second"], observed);
        Assert.Equal("second", local.Value);
    }

    [Fact]
    public async Task ShutdownMayDropAcceptedPostsAndRejectsLaterPosts()
    {
        using var loop = new MessageLoopThread();
        DirectNDispatcher dispatcher = loop.Queue;
        using var gate = new ManualResetEventSlim();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Queue.Post(_ =>
        {
            gate.Wait(Timeout);
            loop.Queue.Close();
            closed.SetResult();
        }, null);
        var ran = false;
        try
        {
            dispatcher.Post(() => ran = true);
            Assert.True(dispatcher.TryPost(() => ran = true));
        }
        finally
        {
            gate.Set();
        }

        await closed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(ran);
        Assert.False(dispatcher.TryPost(() => Assert.Fail("Rejected work ran")));
        Assert.Throws<InvalidOperationException>(() => dispatcher.Post(() => { }));
    }

    [Fact]
    public void NullCallbacksAreRejected()
    {
        using var loop = new MessageLoopThread();
        DirectNDispatcher dispatcher = loop.Queue;
        Assert.Throws<ArgumentNullException>(() => dispatcher.Post(null!));
        Assert.Throws<ArgumentNullException>(() => dispatcher.TryPost(null!));
    }

    [Fact]
    public void OwnerThreadHandleAcquisitionFailsExplicitlyForAnInvalidThread()
    {
        Assert.Throws<Win32Exception>(() => DirectNMessageQueue.OpenOwnerThreadHandle(0));
        using var handle = DirectNMessageQueue.OpenOwnerThreadHandle(global::DirectN.Functions.GetCurrentThreadId());
        Assert.False(handle.IsInvalid);
    }
}
