using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scarlet.Extensions.Hosting.DirectN.Internal;

/// <summary>
/// Starts the DirectN UI thread in <see cref="StartedAsync"/>, i.e. only after every hosted service has started successfully (like
/// ASP.NET Core starts serving requests): if any hosted service fails to start, the UI is never created. Stops it in
/// <see cref="StoppingAsync"/>, i.e. before any hosted service's StopAsync (started last, stopped first), and waits for the UI thread
/// to end, so application cleanup runs while the other hosted services and all registered services still work.
/// </summary>
internal sealed class DirectNHostedService(ILogger<DirectNHostedService> logger, DirectNThread thread) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        logger.DirectNStarting();
        thread.Start();
        logger.DirectNThreadStarted();

        try
        {
            // Wait until the application is initialized and its message loop runs, so failures until then fail host startup
            // instead of crashing the process.
            await thread.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Startup failed or was cancelled: the host will be disposed without stopping its services. End the UI thread now,
            // while the host's services still work.
            thread.StopAndWait();
            throw;
        }
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (thread.RequestExit())
        {
            logger.DirectNStopping();
        }

        // Exiting only queues the exit. Wait until the UI thread has ended, i.e. windows were closed, OnExit ran and the components
        // were disposed, while the other hosted services still run and no service has been disposed yet.
        try
        {
            await thread.Completed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown timeout elapsed; disposing the host still stops and waits for the UI thread (DirectNThread.Dispose).
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
