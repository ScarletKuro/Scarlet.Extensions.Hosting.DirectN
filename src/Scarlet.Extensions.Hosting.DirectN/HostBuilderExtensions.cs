using Microsoft.Extensions.Hosting;

namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// <see cref="IHostBuilder"/> shortcuts. With <c>Host.CreateApplicationBuilder</c> use the
/// <see cref="ServiceCollectionExtensions"/> on <c>builder.Services</c> instead.
/// </summary>
public static class HostBuilderExtensions
{
    /// <summary>
    /// Uses <see cref="DirectNLifetime"/> as the host lifetime instead of the default ConsoleLifetime.
    /// </summary>
    /// <param name="hostBuilder">The <see cref="IHostBuilder" /> to configure.</param>
    /// <returns>The same instance of the <see cref="IHostBuilder"/> for chaining.</returns>
    public static IHostBuilder UseDirectNLifetime(this IHostBuilder hostBuilder)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        return hostBuilder.ConfigureServices(static (_, services) => services.AddDirectNLifetime());
    }

    /// <summary>
    /// Uses <see cref="DirectNLifetime"/> as the host lifetime instead of the default ConsoleLifetime.
    /// </summary>
    /// <param name="hostBuilder">The <see cref="IHostBuilder" /> to configure.</param>
    /// <param name="configureOptions">Configures the <see cref="DirectNLifetimeOptions"/>.</param>
    /// <returns>The same instance of the <see cref="IHostBuilder"/> for chaining.</returns>
    public static IHostBuilder UseDirectNLifetime(this IHostBuilder hostBuilder, Action<DirectNLifetimeOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return hostBuilder.ConfigureServices((_, services) => services.AddDirectNLifetime(configureOptions));
    }
}
