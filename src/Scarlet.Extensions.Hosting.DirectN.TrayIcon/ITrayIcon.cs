namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// A tray icon component: created and initialized on the DirectN UI thread before the message loop starts, and disposed there
/// after it ends. Register it with <see cref="ServiceCollectionExtensions.AddDirectNTrayIcon{TTrayIcon}(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
public interface ITrayIcon : IDirectNComponent, IDisposable
{
}
