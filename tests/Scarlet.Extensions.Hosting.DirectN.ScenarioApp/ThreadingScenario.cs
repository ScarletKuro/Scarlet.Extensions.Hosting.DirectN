using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scarlet.Extensions.Hosting.DirectN.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.ScenarioApp;

/// <summary>
/// JoinableTaskFactory on the DirectN UI thread: switching before and after it starts, Run on it, AsyncLazy.
/// </summary>
internal static class ThreadingScenario
{
    public static void Configure(IServiceCollection services)
    {
        services.AddDirectN(o => o.ShutdownMode = ShutdownMode.OnExplicitShutdown);
        services.AddThreadSwitching();
        services.AddHostedService<EarlySwitchService>();
        services.AddHostedService(sp => new OnStartedService(sp.GetRequiredService<IHostApplicationLifetime>(), sp, sp => RunAsync(sp).GetAwaiter().GetResult()));
    }

    private static async Task RunAsync(IServiceProvider services)
    {
        var jtf = services.GetRequiredService<JoinableTaskFactory>();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();

        await EarlySwitchService.Switched.ConfigureAwait(false);

        await jtf.SwitchToMainThreadAsync();
        Program.Report("SwitchedToMain", Program.Where());

        // Run on the UI thread, with work that needs the UI thread again after leaving it: must not deadlock.
        var value = jtf.Run(async () =>
        {
            await Task.Delay(10).ConfigureAwait(false);
            await jtf.SwitchToMainThreadAsync();
            return Program.Where();
        });
        Program.Report("RunOnMain", value);

        var factoryCalls = 0;
        var lazy = new AsyncLazy<string>(
            async () =>
            {
                Interlocked.Increment(ref factoryCalls);
                await jtf.SwitchToMainThreadAsync();
                return Program.Where();
            },
            jtf);

        await TaskScheduler.Default;
        var fromBackground = Task.Run(() => lazy.GetValueAsync());
        await jtf.SwitchToMainThreadAsync();
        var fromMain = jtf.Run(() => lazy.GetValueAsync());
        Program.Report("AsyncLazy", $"{fromMain}:{await fromBackground}:{factoryCalls}");

        await TaskScheduler.Default;
        Program.Report("BackToBackground", Program.Where());
        lifetime.StopApplication();
    }
}

/// <summary>
/// Switches to the UI thread from StartAsync, i.e. before the UI thread exists.
/// </summary>
internal sealed class EarlySwitchService(JoinableTaskFactory jtf) : IHostedService
{
    private static readonly TaskCompletionSource s_switched = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Switched => s_switched.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            await jtf.SwitchToMainThreadAsync(cancellationToken);
            Program.Report("EarlySwitch", Program.Where());
            s_switched.SetResult();
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
