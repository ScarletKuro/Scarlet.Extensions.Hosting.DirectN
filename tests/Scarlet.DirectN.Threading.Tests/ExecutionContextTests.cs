using System.Globalization;

namespace Scarlet.DirectN.Threading.Tests;

/// <summary>
/// Work runs in the ExecutionContext of whoever queued it (as with WPF's Dispatcher and WinForms' BeginInvoke), and what a
/// callback changes there doesn't leak into later callbacks.
/// </summary>
public class ExecutionContextTests
{
    private static readonly AsyncLocal<string?> Request = new();

    [Fact]
    public async Task EachPostRunsInItsCallersContext()
    {
        using var loop = new MessageLoopThread();
        var seenA = Post(loop, "request-A");
        var seenB = Post(loop, "request-B");

        Assert.Equal("request-A", await seenA.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("request-B", await seenB.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangesMadeByACallbackDontLeakIntoTheNextOnes()
    {
        using var loop = new MessageLoopThread();
        var mutated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            Request.Value = "request-A";
            loop.Queue.Post(_ =>
            {
                Request.Value = "left-by-A";
                mutated.SetResult();
            }, null);
        }, TestContext.Current.CancellationToken);
        await mutated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var fromB = Post(loop, "request-B");
        var withoutValue = Post(loop, null);
        var suppressed = PostWithFlowSuppressed(loop, "suppressed-caller");

        Assert.Equal("request-B", await fromB.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Null(await withoutValue.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Flow suppressed: the caller's value doesn't flow, and A's change doesn't show up either (the UI thread's baseline).
        Assert.Null(await suppressed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrossThreadSendRunsInItsCallersContextAndDoesntLeak()
    {
        using var loop = new MessageLoopThread();

        var seen = await Task.Run(() =>
        {
            Request.Value = "request-Send";
            string? value = null;
            loop.Queue.SynchronizationContext.Send(_ =>
            {
                value = Request.Value;
                Request.Value = "left-by-Send";
            }, null);
            return value;
        }, TestContext.Current.CancellationToken);

        Assert.Equal("request-Send", seen);
        Assert.Null(await Post(loop, null).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CultureFlowsWithTheContext()
    {
        using var loop = new MessageLoopThread();

        var culture = await Task.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("et-EE");
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            loop.Queue.Post(_ => seen.SetResult(CultureInfo.CurrentCulture.Name), null);
            return seen.Task;
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("et-EE", culture);
    }

    private static Task<string?> Post(MessageLoopThread loop, string? value)
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Request.Value = value;
            loop.Queue.Post(_ => seen.SetResult(Request.Value), null);
        });
        thread.Start();
        thread.Join();
        return seen.Task;
    }

    private static Task<string?> PostWithFlowSuppressed(MessageLoopThread loop, string value)
    {
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Request.Value = value;
            using (ExecutionContext.SuppressFlow())
            {
                loop.Queue.Post(_ => seen.SetResult(Request.Value), null);
            }
        });
        thread.Start();
        thread.Join();
        return seen.Task;
    }
}
