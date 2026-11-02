using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Scarlet.Extensions.Hosting.DirectN.Internal;

namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// Registers the DirectN application with an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Runs a DirectN application on a dedicated UI thread inside the host. The UI thread starts after every hosted service has
    /// started, and the host stops when the application exits (and vice versa). Calling it again only adds configuration.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectN(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DirectNOptions>();
        services.TryAddSingleton<DirectNContext>();
        services.TryAddSingleton<DirectNThread>();
        services.TryAddSingleton<IDirectNThread>(static provider => provider.GetRequiredService<DirectNThread>());
        services.TryAddSingleton<IDirectNContext>(static provider => provider.GetRequiredService<DirectNContext>());
        services.TryAddSingleton<DirectNStartupGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DirectNHostedService>());

        return services;
    }

    /// <summary>
    /// Runs a DirectN application on a dedicated UI thread inside the host. See <see cref="AddDirectN(IServiceCollection)"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configureOptions">Configures the <see cref="DirectNOptions"/>.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectN(this IServiceCollection services, Action<DirectNOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddDirectN();
        services.Configure(configureOptions);

        return services;
    }

    /// <summary>
    /// Adds a component created and initialized on the UI thread before the message loop starts, and disposed there after it ends.
    /// The component is created with its dependencies from the container (not resolved as a service, so the container never
    /// disposes it). Requires <see cref="AddDirectN(IServiceCollection)"/>.
    /// </summary>
    /// <typeparam name="TComponent">The component type.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNComponent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TComponent>(this IServiceCollection services)
        where TComponent : class, IDirectNComponent
    {
        ArgumentNullException.ThrowIfNull(services);

        // Not AddSingleton<IDirectNComponent, TComponent>(): the container would dispose it, on a non-UI thread
        // (https://github.com/dotnet/runtime/issues/36491). The UI thread disposes components itself.
        services.AddSingleton<Func<IDirectNComponent>>(static provider => () => ActivatorUtilities.CreateInstance<TComponent>(provider));

        return services;
    }

    /// <summary>
    /// Adds a component created by <paramref name="createComponent"/> on the UI thread before the message loop starts, and disposed
    /// there after it ends. Requires <see cref="AddDirectN(IServiceCollection)"/>.
    /// </summary>
    /// <typeparam name="TComponent">The component type.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="createComponent">Creates the component.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNComponent<TComponent>(this IServiceCollection services, Func<IServiceProvider, TComponent> createComponent)
        where TComponent : class, IDirectNComponent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createComponent);

        services.AddSingleton<Func<IDirectNComponent>>(provider => () => createComponent(provider));

        return services;
    }

    /// <summary>
    /// Uses <see cref="DirectNLifetime"/> as the host lifetime instead of the default ConsoleLifetime. Optional: with any lifetime,
    /// AddDirectN already stops the host when the DirectN application exits.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNLifetime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostLifetime, DirectNLifetime>();

        return services;
    }

    /// <summary>
    /// Uses <see cref="DirectNLifetime"/> as the host lifetime instead of the default ConsoleLifetime.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configureOptions">Configures the <see cref="DirectNLifetimeOptions"/>.</param>
    /// <returns>The same instance of the <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDirectNLifetime(this IServiceCollection services, Action<DirectNLifetimeOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddDirectNLifetime();
        services.Configure(configureOptions);

        return services;
    }
}
