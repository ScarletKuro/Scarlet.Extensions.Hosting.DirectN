using Microsoft.Extensions.DependencyInjection;

namespace Scarlet.Extensions.Hosting.DirectN.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task DisposingBeforeStartupFaultsBufferedAndFutureInvocations()
    {
        var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDirectNThread>().Dispatcher;
        var task = dispatcher.InvokeAsync(() => Assert.Fail("UI never started"), TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        Assert.False(dispatcher.CheckAccess());
        provider.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BufferedInvocationCanBeCanceledBeforeStartup()
    {
        using var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = provider.GetRequiredService<IDirectNThread>().Dispatcher.InvokeAsync(() => Assert.Fail("Canceled work ran"), cancel.Token);
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }
}
