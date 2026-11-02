using DirectN.Extensions.Utilities;

namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// State of the DirectN application run by the host.
/// </summary>
public interface IDirectNContext
{
    /// <summary>
    /// Gets a value indicating whether the DirectN message loop is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the DirectN <see cref="global::DirectN.Extensions.Utilities.Application"/> the host created on the UI thread,
    /// or <see langword="null"/> before it exists. For advanced use (windows list, accelerators); the host owns its lifetime.
    /// </summary>
    Application? Application { get; }
}
