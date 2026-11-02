using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// Registers tray icons with an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a tray icon, created with its dependencies from the container on the DirectN UI thread. The UI thread disposes it
    /// after the message loop ends; the container never does. Requires <c>AddDirectN()</c>.
    /// </summary>
    /// <typeparam name="TTrayIcon">The tray icon type.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNTrayIcon<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTrayIcon>(this IServiceCollection services)
        where TTrayIcon : class, ITrayIcon
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddDirectNComponent<TTrayIcon>();
    }

    /// <summary>
    /// Adds a tray icon created by <paramref name="createTrayIcon"/> on the DirectN UI thread. Requires <c>AddDirectN()</c>.
    /// </summary>
    /// <typeparam name="TTrayIcon">The tray icon type.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="createTrayIcon">Creates the tray icon.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNTrayIcon<TTrayIcon>(this IServiceCollection services, Func<IServiceProvider, TTrayIcon> createTrayIcon)
        where TTrayIcon : class, ITrayIcon
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createTrayIcon);

        return services.AddDirectNComponent(createTrayIcon);
    }
}
