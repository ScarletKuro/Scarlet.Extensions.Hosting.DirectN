# Scarlet.Extensions.Hosting.DirectN

[Microsoft.Extensions.Hosting](https://learn.microsoft.com/dotnet/core/extensions/generic-host) (Generic Host) for
[DirectN](https://github.com/smourier/DirectNAot) Win32 / Direct2D desktop apps. It runs the DirectN UI thread inside the
host, the way [Extensions.Hosting.Wpf](https://github.com/ScarletKuro/Microsoft.Extensions.Hosting.Wpf) does for WPF, and
everything is **Native AOT compatible** (`net10.0-windows`, `IsAotCompatible`, CI publishes with `PublishAot`).

| Package | What it is |
|---|---|
| `Scarlet.Extensions.Hosting.DirectN` | The host integration: UI thread, lifetime, components. |
| `Scarlet.Extensions.Hosting.DirectN.Threading` | `JoinableTaskFactory` / `AsyncLazy<T>` bound to the UI thread ([docs](docs/Threading.md)). |
| `Scarlet.Extensions.Hosting.DirectN.TrayIcon` | Tray icon with WinForms `NotifyIcon` behavior and a native menu ([docs](docs/TrayIcon.md)). |
| `Scarlet.DirectN.Threading` | The UI-thread message queue and `SynchronizationContext`, shared with `Scarlet.AspNetCore.Components.WebView.Win32Aot` ([docs](docs/MessageQueue.md)). |

Coming from Extensions.Hosting.Wpf? See [Migrating from WPF](docs/MigratingFromWpf.md).

## Getting started

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDirectN(options =>
{
    options.ShutdownMode = ShutdownMode.OnLastWindowClose;   // or OnExplicitShutdown for tray apps
    options.OnStartup = (services, context) => { /* UI thread, before the message loop */ };
    options.OnExit = (services, context) => { /* UI thread, after the message loop */ };
});
builder.Services.AddDirectNLifetime();                        // optional: windowed-app lifetime instead of ConsoleLifetime
builder.Services.AddThreadSwitching();                        // optional: JoinableTaskFactory on the UI thread
builder.Services.AddDirectNComponent<MainWindowComponent>();  // created on the UI thread

await builder.Build().RunAsync();

sealed class MainWindowComponent : IDirectNComponent
{
    public void InitializeComponent()
    {
        var window = new Window("My app");                    // DirectN.Extensions.Utilities.Window
        window.ResizeClient(800, 600);
        window.Center();
        window.Show();
    }
}
```

## How it works

- `AddDirectN()` creates a dedicated STA UI thread (`IDirectNThread.MainThread`). It starts **after every hosted service has
  started** (in `IHostedLifecycleService.StartedAsync`), so a failing service means no UI is ever created.
- On the UI thread the host creates the message queue, a DirectN `Application`, runs `OnStartup`, creates and initializes the
  components in registration order, then runs the message loop. Failures until the loop runs fail host startup
  (`RunAsync`/`StartAsync` throws) instead of crashing the process.
- When the application exits on its own (last window closed with `OnLastWindowClose`, `Application.Exit()`, a fatal DirectN
  error), the host stops. When the host stops (`StopApplication()`, `IDirectNThread.HandleApplicationExit()`, process exit
  with `DirectNLifetime`), the application exits. Either way the host waits for the UI thread: remaining windows are closed,
  `OnExit` runs and components are disposed (in reverse order) **on the UI thread, while all services still work**.
- `IDirectNThread.SynchronizationContext` works at any time: work posted before the UI thread starts runs once it has
  started; work posted after it ended is dropped and logged, never thrown.
- Exceptions thrown by work posted to the UI thread raise `IDirectNThread.UnhandledException`; set `Handled` to keep going,
  otherwise they are logged and terminate the process (like WPF's `DispatcherUnhandledException`).

## API

| Member | |
|---|---|
| `services.AddDirectN()` / `AddDirectN(Action<DirectNOptions>)` | Runs the DirectN application in the host. Calling it again only adds configuration. |
| `DirectNOptions.ShutdownMode` | `OnLastWindowClose` (default; DirectN marks windows created after the first as background windows, so in practice this is the main window) or `OnExplicitShutdown`. |
| `DirectNOptions.OnStartup` / `OnExit` | Callbacks on the UI thread. |
| `DirectNOptions.ShowFatalErrorDialog` | Keep DirectN's own fatal-error task dialog (default `false`: the host logs instead). |
| `services.AddDirectNComponent<T>()` / `AddDirectNComponent(factory)` | A component created, initialized and disposed on the UI thread; never disposed by the container. |
| `services.AddDirectNLifetime()` / `hostBuilder.UseDirectNLifetime()` | `DirectNLifetime` instead of `ConsoleLifetime` (no Ctrl+C handling, status messages, graceful stop on process exit). |
| `IDirectNThread` | `MainThread`, `SynchronizationContext`, `Context`, `HandleApplicationExit()`, `UnhandledException`. |
| `IDirectNContext` | `IsRunning`, `Application` (the DirectN `Application`, for advanced use). |

One DirectN host per process: DirectN allows one `Application` per thread and none after the process-wide exit.

## Samples

- [`samples/HostingMinimal`](samples/HostingMinimal): a window whose title a `BackgroundService` updates through `JoinableTaskFactory`.
- [`samples/HostingTrayIcon`](samples/HostingTrayIcon): a windowless tray app with a menu, balloon tips and Exit.

Both publish with `PublishAot`.

## License

MIT. `Scarlet.Extensions.Hosting.DirectN.Threading` contains code from
[microsoft/vs-threading](https://github.com/microsoft/vs-threading) (MIT) and the tray icon ports behavior from
[dotnet/winforms](https://github.com/dotnet/winforms) (MIT); see [LICENSE-3RD-PARTY.txt](LICENSE-3RD-PARTY.txt) and
[UPSTREAM.md](UPSTREAM.md).
