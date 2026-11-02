using DirectN;
using DirectN.Extensions.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scarlet.Extensions.Hosting.DirectN;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon;

using IServiceProvider = System.IServiceProvider;

namespace Scarlet.Extensions.Hosting.DirectN.ScenarioApp;

/// <summary>
/// Runs one hosting scenario (args[0]) and reports what happened as <c>EVT|name|value</c> lines on stdout.
/// Exit codes: 0 host stopped normally, 2 unknown scenario, 3 host failed to start, 4 timeout; anything else is a crash.
/// </summary>
internal static class Program
{
    public const int UnknownScenarioExitCode = 2;
    public const int HostFailedExitCode = 3;
    public const int TimeoutExitCode = 4;

    private static readonly Lock ReportLock = new();
    private static Thread? s_uiThread;

    public static int Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : string.Empty;
        StartWatchdog(TimeSpan.FromSeconds(30));

        var builder = Host.CreateApplicationBuilder(args.Skip(1).ToArray());
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddDirectNLifetime();

        if (!Scenarios.TryConfigure(scenario, builder.Services))
        {
            Report("UnknownScenario", scenario);
            return UnknownScenarioExitCode;
        }

        using var host = builder.Build();
        var thread = host.Services.GetRequiredService<IDirectNThread>();
        s_uiThread = thread.MainThread;

        if (scenario == "canceled-during-init")
        {
            // Startup is cancelled while a component is still initializing: the message loop must never run.
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                host.StartAsync(cancel.Token).GetAwaiter().GetResult();
                Report("Started");
            }
            catch (OperationCanceledException)
            {
                Report("StartCanceled", thread.Context.IsRunning ? "running" : "not-running");
            }

            return 0;
        }

        try
        {
            host.Run();
        }
#pragma warning disable CA1031 // Reported to the test.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Report("HostFailed", $"{exception.GetType().Name}: {exception.Message}");
            return HostFailedExitCode;
        }

        Report("HostStopped", thread.Context.IsRunning ? "running" : "not-running");
        Scenarios.AfterRun(scenario, thread);
        return 0;
    }

    /// <summary>
    /// "ui" on the DirectN UI thread, "other" elsewhere.
    /// </summary>
    public static string Where() => Thread.CurrentThread == s_uiThread ? "ui" : "other";

    public static void Report(string name, string? value = null)
    {
        lock (ReportLock)
        {
            Console.Out.WriteLine($"EVT|{name}|{value}");
            Console.Out.Flush();
        }
    }

    private static void StartWatchdog(TimeSpan timeout)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(timeout);
            Report("Timeout");
            Environment.Exit(TimeoutExitCode);
        })
        {
            IsBackground = true,
            Name = "Watchdog",
        };
        watchdog.Start();
    }
}

/// <summary>
/// Reports its lifecycle and on which thread it happens.
/// </summary>
internal sealed class ProbeComponent : IDirectNComponent, IDisposable
{
    public void InitializeComponent() => Program.Report("ComponentInit", Program.Where());

    public void Dispose() => Program.Report("ComponentDispose", Program.Where());
}

/// <summary>
/// Fails to initialize.
/// </summary>
internal sealed class FailingComponent : IDirectNComponent, IDisposable
{
    public void InitializeComponent()
    {
        Program.Report("FailingComponentInit", Program.Where());
        throw new InvalidOperationException("boom");
    }

    public void Dispose() => Program.Report("FailingComponentDispose", Program.Where());
}

/// <summary>
/// Creates a DirectN window (not shown).
/// </summary>
internal sealed class WindowComponent : IDirectNComponent
{
    public static Window? Window { get; private set; }

    public void InitializeComponent()
    {
        Window = new Window("Scarlet scenario window");
        Program.Report("WindowCreated", Program.Where());
    }
}

/// <summary>
/// A tray icon with a menu, using a shared system icon.
/// </summary>
internal sealed class ScenarioTrayIcon(IDirectNThread thread) : ITrayIcon
{
    private TrayNotifyIcon? _notifyIcon;
    private TrayIconImage? _image;

    public void InitializeComponent()
    {
        var systemIcon = Functions.LoadImageW(default, Constants.IDI_APPLICATION, GDI_IMAGE_TYPE.IMAGE_ICON, 0, 0, IMAGE_FLAGS.LR_SHARED);
        _image = TrayIconImage.FromHandle(systemIcon.Value, ownsHandle: false);
        _notifyIcon = new TrayNotifyIcon
        {
            Icon = _image,
            Text = $"Scenario {ApplicationInfo.ProductVersion}",
            ShowContextMenuOnLeftClick = true,
            ContextMenu = new TrayMenu
            {
                Items =
                {
                    new TrayMenuItem(ApplicationInfo.ProductVersion) { Enabled = false },
                    TrayMenuItem.Separator(),
                    new TrayMenuItem("E&xit", (_, _) => thread.HandleApplicationExit()),
                },
            },
            Visible = true,
        };
        Program.Report("TrayInit", Program.Where());
        Program.Report("ProductVersion", ApplicationInfo.ProductVersion);
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
        _image?.Dispose();
        Program.Report("TrayDispose", Program.Where());
    }
}

/// <summary>
/// Runs an action once the host has started, off the UI thread.
/// </summary>
internal sealed class OnStartedService(IHostApplicationLifetime lifetime, IServiceProvider services, Action<IServiceProvider> action) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => Task.Run(() => action(services)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Posts to the UI thread from StartAsync, i.e. before the UI thread exists.
/// </summary>
internal sealed class PrestartPostService(IDirectNThread thread) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Program.Report("PrestartPosting", thread.Context.IsRunning ? "running" : "not-running");
        thread.SynchronizationContext.Post(_ =>
        {
            Program.Report("PrestartPostRan", Program.Where());
        }, null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A service a component uses until it is disposed.
/// </summary>
internal sealed class GuardedDependency : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        Program.Report("DependencyDisposed", Program.Where());
    }
}

/// <summary>
/// Reports whether its dependency is still usable when the UI thread disposes it.
/// </summary>
internal sealed class DependentComponent(GuardedDependency dependency) : IDirectNComponent, IDisposable
{
    public void InitializeComponent() => Program.Report("DependentComponentInit", Program.Where());

    public void Dispose() => Program.Report("DependentComponentDispose", $"{Program.Where()}:{(dependency.Disposed ? "dependency-disposed" : "dependency-alive")}");
}

/// <summary>
/// Fails after the UI thread has started: the host then disposes its services without stopping them.
/// </summary>
internal sealed class FailingStartedService : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("started failure");

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Sends to the UI thread from StartAsync, before it exists: must throw instead of deadlocking the host.
/// </summary>
internal sealed class SendBeforeStartService(IDirectNThread thread) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Scenarios.SendAndReport(thread.SynchronizationContext, "SendBeforeStart");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Posts from StartAsync, before the UI thread exists, with an AsyncLocal set: the work must still see it.
/// </summary>
internal sealed class ContextPostService(IDirectNThread thread) : IHostedService
{
    public static AsyncLocal<string?> Request { get; } = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Request.Value = "prestart-caller";
        thread.SynchronizationContext.Post(_ => Program.Report("PrestartPost", Request.Value), null);
        Request.Value = null;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Initializes slower than the host is willing to wait for startup.
/// </summary>
internal sealed class SlowComponent : IDirectNComponent, IDisposable
{
    public void InitializeComponent()
    {
        Program.Report("SlowInitStarted", Program.Where());
        Thread.Sleep(1500);
        Program.Report("SlowInitFinished", Program.Where());
    }

    public void Dispose() => Program.Report("SlowComponentDispose", Program.Where());
}

/// <summary>
/// A DirectN window whose window procedure throws for <see cref="FailMessage"/>, and optionally whose Dispose throws.
/// </summary>
internal sealed class FaultyWindow(bool throwOnDispose) : global::DirectN.Extensions.Utilities.Window("Scarlet faulty window")
{
    public const uint FailMessage = 0x8000 + 77;

    protected override global::DirectN.LRESULT? WindowProc(global::DirectN.HWND hwnd, uint msg, global::DirectN.WPARAM wParam, global::DirectN.LPARAM lParam)
    {
        if (msg == FailMessage)
        {
            throw new InvalidOperationException("window failure");
        }

        return base.WindowProc(hwnd, msg, wParam, lParam);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && throwOnDispose)
        {
            throw new InvalidOperationException("window dispose failure");
        }
    }
}

/// <summary>
/// Creates a <see cref="FaultyWindow"/>; optionally makes it fail before the message loop has run.
/// </summary>
internal sealed class FaultyWindowComponent(bool failBeforeStart, bool throwOnDispose) : IDirectNComponent
{
    public static FaultyWindow? Window { get; private set; }

    public void InitializeComponent()
    {
        Window = new FaultyWindow(throwOnDispose);
        Program.Report("FaultyWindowCreated", Program.Where());
        if (failBeforeStart)
        {
            // Fails synchronously during initialization, before the message loop has run.
            global::DirectN.Functions.SendMessageW(Window.Handle, FaultyWindow.FailMessage, default, default);
        }
    }
}

/// <summary>
/// Throws from Dispose.
/// </summary>
internal sealed class ThrowingDisposeComponent : IDirectNComponent, IDisposable
{
    public void InitializeComponent()
    {
    }

    public void Dispose()
    {
        Program.Report("ThrowingDispose", Program.Where());
        throw new InvalidOperationException("component dispose failure");
    }
}
