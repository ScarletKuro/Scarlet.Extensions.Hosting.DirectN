using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scarlet.DirectN.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.ScenarioApp;

internal static class InvokeAsyncScenario
{
    private static Task<string>? s_earlyInvocation;
    private static readonly AsyncLocal<string?> CallerValue = new();

    public static void Configure(IServiceCollection services, bool failStartup)
    {
        services.AddDirectN(o =>
        {
            o.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (failStartup)
            {
                o.OnStartup = (_, _) => throw new InvalidOperationException("invocation startup failure");
            }
        });
        services.AddHostedService<EarlyInvocationService>();
        services.AddHostedService(sp => new OnStartedService(sp.GetRequiredService<IHostApplicationLifetime>(), sp,
            sp => RunAsync(sp).GetAwaiter().GetResult()));
    }

    private static async Task RunAsync(IServiceProvider services)
    {
        Program.Report("EarlyInvocation", await s_earlyInvocation!.ConfigureAwait(false));
        var dispatcher = services.GetRequiredService<DirectNDispatcher>();
        var result = await dispatcher.InvokeAsync(async () =>
        {
            dispatcher.VerifyAccess();
            await Task.Yield();
            dispatcher.VerifyAccess();
            return Program.Where();
        }).ConfigureAwait(false);
        Program.Report("AsyncInvocation", result);
        try
        {
            await dispatcher.InvokeAsync((Action)(() => throw new FormatException("observed"))).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            Program.Report("InvocationFailure", ex.Message);
        }

        services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    }

    public static void ReportStartupFailure()
    {
        try
        {
            s_earlyInvocation!.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Program.Report("BufferedInvocation", "unexpected success");
        }
        catch (InvalidOperationException)
        {
            Program.Report("BufferedInvocation", "faulted");
        }
    }

    private sealed class EarlyInvocationService(IDirectNThread thread) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            CallerValue.Value = "early-caller";
            s_earlyInvocation = thread.Dispatcher.InvokeAsync(() =>
            {
                thread.Dispatcher.VerifyAccess();
                return $"{Program.Where()}:{thread.Context.IsRunning}:{CallerValue.Value}";
            }, cancellationToken);
            CallerValue.Value = null;
            // The UI starts after hosted services: retain the task without awaiting it here.
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
