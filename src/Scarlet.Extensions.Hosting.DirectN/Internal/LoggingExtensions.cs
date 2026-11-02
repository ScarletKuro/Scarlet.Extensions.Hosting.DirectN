using Microsoft.Extensions.Logging;

namespace Scarlet.Extensions.Hosting.DirectN.Internal;

internal static partial class LoggingExtensions
{
    [LoggerMessage(0, LogLevel.Information, "Starting DirectN application")]
    internal static partial void DirectNStarting(this ILogger logger);

    [LoggerMessage(1, LogLevel.Information, "DirectN UI thread started")]
    internal static partial void DirectNThreadStarted(this ILogger logger);

    [LoggerMessage(2, LogLevel.Information, "Stopping DirectN application due to host shutdown")]
    internal static partial void DirectNStopping(this ILogger logger);

    [LoggerMessage(3, LogLevel.Information, "DirectN application started")]
    internal static partial void DirectNApplicationStarted(this ILogger logger);

    [LoggerMessage(4, LogLevel.Information, "DirectN application is shutting down...")]
    internal static partial void DirectNApplicationShuttingDown(this ILogger logger);

    [LoggerMessage(5, LogLevel.Information, "DirectN application was stopped")]
    internal static partial void DirectNApplicationStopped(this ILogger logger);

    [LoggerMessage(6, LogLevel.Information, "Waiting for the host to be disposed, please ensure all 'IHost' instances are wrapped in 'using' blocks")]
    internal static partial void WaitingHost(this ILogger logger);

    [LoggerMessage(7, LogLevel.Information, "Lifetime: {lifetime}")]
    internal static partial void LifeTime(this ILogger logger, string lifetime);

    [LoggerMessage(8, LogLevel.Information, "Hosting environment: {envName}")]
    internal static partial void HostingEnvironment(this ILogger logger, string envName);

    [LoggerMessage(9, LogLevel.Information, "Content root path: {contentRoot}")]
    internal static partial void ContentRootPath(this ILogger logger, string contentRoot);

    [LoggerMessage(10, LogLevel.Warning, "Work was posted to the DirectN UI thread before it started; it runs once the DirectN application has started. The UI thread starts after every hosted service has started (StartAsync)")]
    internal static partial void DirectNPostQueued(this ILogger logger);

    [LoggerMessage(11, LogLevel.Warning, "{count} work item(s) posted to the DirectN UI thread never ran because the DirectN application was not started")]
    internal static partial void DirectNQueuedPostsDropped(this ILogger logger, int count);

    [LoggerMessage(12, LogLevel.Warning, "Work posted to the DirectN UI thread was dropped because the UI thread has ended")]
    internal static partial void DirectNPostDropped(this ILogger logger);

    [LoggerMessage(13, LogLevel.Critical, "Unhandled exception on the DirectN UI thread")]
    internal static partial void DirectNUnhandledException(this ILogger logger, Exception exception);

    [LoggerMessage(14, LogLevel.Error, "DirectN reported an error")]
    internal static partial void DirectNError(this ILogger logger, Exception exception);

    [LoggerMessage(15, LogLevel.Debug, "Application.Exit() was cancelled by an ApplicationExiting handler; the host forces the exit")]
    internal static partial void DirectNExitCancelled(this ILogger logger);
}
