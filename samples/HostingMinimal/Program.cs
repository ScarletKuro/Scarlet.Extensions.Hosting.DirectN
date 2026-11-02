using DirectN.Extensions.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scarlet.Extensions.Hosting.DirectN;
using Scarlet.Extensions.Hosting.DirectN.Threading;

// A DirectN window hosted by the Generic Host. Closing the window stops the host (ShutdownMode.OnLastWindowClose);
// a background service updates the window title through JoinableTaskFactory.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDirectN();
builder.Services.AddDirectNLifetime();
builder.Services.AddThreadSwitching();
builder.Services.AddSingleton<MainWindowHolder>();
builder.Services.AddDirectNComponent<MainWindowComponent>();
builder.Services.AddHostedService<ClockService>();

await builder.Build().RunAsync();

/// <summary>
/// Lets services reach the window created on the UI thread.
/// </summary>
internal sealed class MainWindowHolder
{
    public Window? Window { get; set; }
}

/// <summary>
/// Creates and shows the main window on the UI thread before the message loop starts.
/// </summary>
internal sealed class MainWindowComponent(MainWindowHolder holder) : IDirectNComponent
{
    public void InitializeComponent()
    {
        var window = new Window("Scarlet.Extensions.Hosting.DirectN");
        window.ResizeClient(640, 360);
        window.Center();
        window.Show();
        holder.Window = window;
    }
}

/// <summary>
/// Runs on the thread pool and switches to the UI thread to touch the window.
/// </summary>
internal sealed partial class ClockService(JoinableTaskFactory jtf, MainWindowHolder holder, ILogger<ClockService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.Now;

                await jtf.SwitchToMainThreadAsync(stoppingToken);
                if (holder.Window is { IsDisposed: false } window)
                {
                    window.Text = $"Scarlet.Extensions.Hosting.DirectN - {now:T}";
                }

                await TaskScheduler.Default;
                LogTick(now);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping (e.g. the window was closed): the normal way out of the loop.
        }
    }

    [LoggerMessage(LogLevel.Debug, "Tick {Time}")]
    private partial void LogTick(DateTime time);
}
