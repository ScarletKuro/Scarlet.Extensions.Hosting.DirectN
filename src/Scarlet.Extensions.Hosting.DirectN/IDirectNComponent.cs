namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// A component living on the DirectN UI thread, e.g. a main window or a tray icon.
/// </summary>
/// <remarks>
/// Components are registered with <c>AddDirectNComponent</c>, created and initialized on the UI thread in registration
/// order before the message loop starts, and, if they implement <see cref="IDisposable"/>, disposed on the UI thread in
/// reverse order after it has ended. The DI container never disposes them.
/// </remarks>
public interface IDirectNComponent
{
    /// <summary>
    /// Initializes the component. Called on the UI thread before the message loop starts.
    /// </summary>
    void InitializeComponent();
}
