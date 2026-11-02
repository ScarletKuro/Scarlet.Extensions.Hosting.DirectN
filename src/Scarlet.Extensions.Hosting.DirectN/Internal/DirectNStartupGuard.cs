namespace Scarlet.Extensions.Hosting.DirectN.Internal;

/// <summary>
/// Resolved by the UI thread once the message loop runs, so the DI container, which disposes in reverse creation order, disposes
/// it before every service resolved during startup (OnStartup, components). If the host never finished starting (a hosted service
/// failed in StartedAsync after the UI thread had started), the host does not stop its services; disposing this guard then stops
/// the application, closing its windows and disposing its components, while those services still work.
/// </summary>
internal sealed class DirectNStartupGuard(DirectNThread thread) : IDisposable
{
    // After a normal stop the UI thread has already ended (StoppingAsync waited for it), so this returns immediately.
    public void Dispose() => thread.StopAndWait();
}
