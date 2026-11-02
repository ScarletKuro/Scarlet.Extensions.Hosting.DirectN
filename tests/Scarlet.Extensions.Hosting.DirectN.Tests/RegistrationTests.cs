using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scarlet.Extensions.Hosting.DirectN.Internal;

namespace Scarlet.Extensions.Hosting.DirectN.Tests;

/// <summary>
/// Registrations and behavior that don't need the UI thread to run.
/// </summary>
public class RegistrationTests
{
    [Fact]
    public void AddDirectNIsIdempotentAndAccumulatesOptions()
    {
        var services = new ServiceCollection();
        services.AddDirectN(o => o.ShutdownMode = ShutdownMode.OnExplicitShutdown);
        services.AddDirectN(o => o.ShowFatalErrorDialog = true);

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DirectNHostedService));
        Assert.Single(services, d => d.ServiceType == typeof(IDirectNThread));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DirectNOptions>>().Value;
        Assert.Equal(ShutdownMode.OnExplicitShutdown, options.ShutdownMode);
        Assert.True(options.ShowFatalErrorDialog);
    }

    [Fact]
    public void ThreadAndContextResolveToTheSameInstances()
    {
        using var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();

        var thread = provider.GetRequiredService<IDirectNThread>();
        Assert.Same(provider.GetRequiredService<DirectNThread>(), thread);
        Assert.Same(provider.GetRequiredService<IDirectNContext>(), thread.Context);
        Assert.Equal(ApartmentState.STA, thread.MainThread.GetApartmentState());
        Assert.True(thread.MainThread.IsBackground);
        Assert.False(thread.Context.IsRunning);
        Assert.Null(thread.Context.Application);
    }

    [Fact]
    public void ComponentsAreRegisteredAsFactoriesInOrderAndNeverResolvedByTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Dependency>();
        services.AddDirectNComponent<ComponentWithDependency>();
        services.AddDirectNComponent(_ => new OtherComponent());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDirectNComponent));

        using var provider = services.BuildServiceProvider();
        var components = provider.GetServices<Func<IDirectNComponent>>().Select(factory => factory()).ToList();
        Assert.IsType<ComponentWithDependency>(components[0]);
        Assert.Same(provider.GetRequiredService<Dependency>(), ((ComponentWithDependency)components[0]).Dependency);
        Assert.IsType<OtherComponent>(components[1]);
    }

    [Fact]
    public void AddDirectNLifetimeReplacesTheHostLifetime()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDirectNLifetime(o => o.SuppressStatusMessages = true);
        using var host = builder.Build();

        Assert.IsType<DirectNLifetime>(host.Services.GetRequiredService<IHostLifetime>());
        Assert.True(host.Services.GetRequiredService<IOptions<DirectNLifetimeOptions>>().Value.SuppressStatusMessages);
    }

    [Fact]
    public void UseDirectNLifetimeOnHostBuilder()
    {
        using var host = Host.CreateDefaultBuilder().UseDirectNLifetime().Build();

        Assert.IsType<DirectNLifetime>(host.Services.GetRequiredService<IHostLifetime>());
    }

    [Fact]
    public void UseDirectNLifetimeOnHostBuilderWithOptions()
    {
        using var host = Host.CreateDefaultBuilder().UseDirectNLifetime(o => o.SuppressStatusMessages = true).Build();

        Assert.IsType<DirectNLifetime>(host.Services.GetRequiredService<IHostLifetime>());
        Assert.True(host.Services.GetRequiredService<IOptions<DirectNLifetimeOptions>>().Value.SuppressStatusMessages);
    }

    [Fact]
    public void RegistrationMethodsRejectNullArguments()
    {
        var services = new ServiceCollection();
        IServiceCollection nullServices = null!;
        IHostBuilder nullBuilder = null!;

        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectN());
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectN(_ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddDirectN(null!));
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNComponent<OtherComponent>());
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNComponent(_ => new OtherComponent()));
        Assert.Throws<ArgumentNullException>(() => services.AddDirectNComponent<OtherComponent>(null!));
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNLifetime());
        Assert.Throws<ArgumentNullException>(() => nullServices.AddDirectNLifetime(_ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddDirectNLifetime(null!));
        Assert.Throws<ArgumentNullException>(() => nullBuilder.UseDirectNLifetime());
        Assert.Throws<ArgumentNullException>(() => nullBuilder.UseDirectNLifetime(_ => { }));
        Assert.Throws<ArgumentNullException>(() => Host.CreateDefaultBuilder().UseDirectNLifetime(null!));
    }

    [Fact]
    public void LifetimeWithoutLoggerFactoryUsesNoLogger()
    {
        var builder = Host.CreateApplicationBuilder();
        using var host = builder.Build();
        var services = host.Services;

        using var lifetime = new DirectNLifetime(
            services.GetRequiredService<IOptions<DirectNLifetimeOptions>>(),
            services.GetRequiredService<IHostEnvironment>(),
            services.GetRequiredService<IHostApplicationLifetime>(),
            services.GetRequiredService<IOptions<HostOptions>>());

        Assert.True(lifetime.WaitForStartAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        Assert.True(lifetime.StopAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    [Fact]
    public void LifetimeRejectsNullArguments()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        var options = host.Services.GetRequiredService<IOptions<DirectNLifetimeOptions>>();
        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>();
        var loggerFactory = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

        Assert.Throws<ArgumentNullException>(() => new DirectNLifetime(null!, environment, lifetime, hostOptions, loggerFactory));
        Assert.Throws<ArgumentNullException>(() => new DirectNLifetime(options, null!, lifetime, hostOptions, loggerFactory));
        Assert.Throws<ArgumentNullException>(() => new DirectNLifetime(options, environment, null!, hostOptions, loggerFactory));
        Assert.Throws<ArgumentNullException>(() => new DirectNLifetime(options, environment, lifetime, null!, loggerFactory));
        Assert.Throws<ArgumentNullException>(() => new DirectNLifetime(options, environment, lifetime, hostOptions, null!));
    }

    [Fact]
    public async Task StartedAsyncWithCancelledTokenDoesntStartTheUiThread()
    {
        using var provider = new ServiceCollection().AddLogging().AddDirectN().BuildServiceProvider();
        var hostedService = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single(s => s is DirectNHostedService);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await hostedService.StartedAsync(cancelled.Token);

        Assert.Equal(ThreadState.Unstarted, provider.GetRequiredService<IDirectNThread>().MainThread.ThreadState & ThreadState.Unstarted);
        await hostedService.StoppingAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void HostSynchronizationContextCopyIsItself()
    {
        using var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();
        var context = provider.GetRequiredService<IDirectNThread>().SynchronizationContext;

        Assert.Same(context, context.CreateCopy());
    }

    [Fact]
    public void ProcessExitStopsTheHostWithoutResettingTheExitCode()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDirectNLifetime();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        using var host = builder.Build();
        var lifetime = (DirectNLifetime)host.Services.GetRequiredService<IHostLifetime>();
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var previousExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 42;
            lifetime.OnProcessExit(null, EventArgs.Empty);

            Assert.True(applicationLifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(42, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public void PostingBeforeStartIsQueuedAndDroppedWithoutThrowingWhenTheHostIsDisposed()
    {
        var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();
        var thread = provider.GetRequiredService<DirectNThread>();
        var ran = false;

        Assert.True(thread.Post(_ => ran = true, null));
        provider.Dispose();

        Assert.False(thread.Post(_ => ran = true, null));
        thread.SynchronizationContext.Post(_ => ran = true, null);
        Assert.False(ran);
    }

    [Fact]
    public void SendAfterTheUiThreadEndedThrows()
    {
        var provider = new ServiceCollection().AddDirectN().BuildServiceProvider();
        var thread = provider.GetRequiredService<IDirectNThread>();
        provider.Dispose();

        Assert.Throws<InvalidOperationException>(() => thread.SynchronizationContext.Send(_ => { }, null));
    }

    [Fact]
    public void HandleApplicationExitIsIgnoredWhenNotRunning()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDirectN();
        using var host = builder.Build();

        host.Services.GetRequiredService<IDirectNThread>().HandleApplicationExit();

        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    private sealed class Dependency;

    private sealed class ComponentWithDependency(Dependency dependency) : IDirectNComponent
    {
        public Dependency Dependency { get; } = dependency;

        public void InitializeComponent()
        {
        }
    }

    private sealed class OtherComponent : IDirectNComponent
    {
        public void InitializeComponent()
        {
        }
    }
}
