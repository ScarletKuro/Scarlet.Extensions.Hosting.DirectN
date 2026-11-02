using Microsoft.Extensions.DependencyInjection;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Tests;

public class RegistrationTests
{
    [Fact]
    public void TrayIconsAreRegisteredAsComponentFactories()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Dependency>();
        services.AddDirectNTrayIcon<TrayIconWithDependency>();
        services.AddDirectNTrayIcon(_ => new OtherTrayIcon());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ITrayIcon));

        using var provider = services.BuildServiceProvider();
        var components = provider.GetServices<Func<IDirectNComponent>>().Select(factory => factory()).ToList();
        Assert.Same(provider.GetRequiredService<Dependency>(), Assert.IsType<TrayIconWithDependency>(components[0]).Dependency);
        Assert.IsType<OtherTrayIcon>(components[1]);
    }

    [Fact]
    public void RegistrationRejectsNullArguments()
    {
        IServiceCollection nullServices = null!;

        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNTrayIcon<OtherTrayIcon>());
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNTrayIcon(_ => new OtherTrayIcon()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddDirectNTrayIcon<OtherTrayIcon>(null!));
    }

    [Fact]
    public void ApplicationInfoReadsTheEntryAssembly()
    {
        Assert.False(string.IsNullOrEmpty(ApplicationInfo.ProductVersion));
        Assert.False(string.IsNullOrEmpty(ApplicationInfo.ProductName));
    }

    private sealed class Dependency;

    private sealed class TrayIconWithDependency(Dependency dependency) : ITrayIcon
    {
        public Dependency Dependency { get; } = dependency;

        public void InitializeComponent()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class OtherTrayIcon : ITrayIcon
    {
        public void InitializeComponent()
        {
        }

        public void Dispose()
        {
        }
    }
}
