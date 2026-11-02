using DirectN.Extensions.Utilities;

namespace Scarlet.Extensions.Hosting.DirectN.Internal;

internal sealed class DirectNContext : IDirectNContext
{
    // Written on the UI thread and read from host threads.
    private volatile bool _isRunning;
    private volatile Application? _application;

    public bool IsRunning
    {
        get => _isRunning;
        internal set => _isRunning = value;
    }

    public Application? Application
    {
        get => _application;
        internal set => _application = value;
    }
}
