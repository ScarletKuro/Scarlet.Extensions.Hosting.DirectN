# Scarlet.Extensions.Hosting.DirectN
[![Nuget](https://img.shields.io/nuget/v/Scarlet.Extensions.Hosting.DirectN?color=ff4081&logo=nuget)](https://www.nuget.org/packages/Scarlet.Extensions.Hosting.DirectN/)
[![Nuget](https://img.shields.io/nuget/dt/Scarlet.Extensions.Hosting.DirectN?color=ff4081&label=nuget%20downloads&logo=nuget)](https://www.nuget.org/packages/Scarlet.Extensions.Hosting.DirectN/)
[![GitHub](https://img.shields.io/github/license/ScarletKuro/Scarlet.Extensions.Hosting.DirectN?color=594ae2&logo=github)](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/blob/main/LICENSE)
[![codecov](https://codecov.io/gh/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/graph/badge.svg?token=BOR54748WR)](https://codecov.io/gh/ScarletKuro/Scarlet.Extensions.Hosting.DirectN)

Unofficial implementation of Microsoft.Extensions.Hosting for [DirectN](https://github.com/smourier/DirectNAot) (Win32 / Direct2D) desktop applications, and the successor of [Extensions.Hosting.Wpf](https://github.com/ScarletKuro/Microsoft.Extensions.Hosting.Wpf) for apps moving off WPF. It runs the DirectN UI thread inside the Generic Host and is **Native AOT compatible** end to end: every package is `IsAotCompatible`, and CI publishes the samples, the scenario app and the tests with `PublishAot`.
This library also has extension packages for thread switching between the UI thread and the thread pool (`JoinableTaskFactory`, `AsyncLazy<T>`) and a tray icon.

### [Changelog](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/blob/main/CHANGELOG.md) | [Wiki](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki)

## NuGet Packages

| Name | Description | Latest version |
| --- | --- | --- |
| **Scarlet.Extensions.Hosting.DirectN** | The main library, with **Microsoft.Extensions.Hosting** for DirectN support. | [![NuGet](https://img.shields.io/nuget/vpre/Scarlet.Extensions.Hosting.DirectN.svg)](https://www.nuget.org/packages/Scarlet.Extensions.Hosting.DirectN/) |
| **Scarlet.Extensions.Hosting.DirectN.TrayIcon** | An extension enabling tray icon support, with WinForms `NotifyIcon` behavior and a native Win32 menu. | [![NuGet](https://img.shields.io/nuget/vpre/Scarlet.Extensions.Hosting.DirectN.TrayIcon.svg)](https://www.nuget.org/packages/Scarlet.Extensions.Hosting.DirectN.TrayIcon/) |
| **Scarlet.Extensions.Hosting.DirectN.Threading** | An extension enabling thread switching between the UI thread and ThreadPool threads via an AOT-compatible extract of **Microsoft.VisualStudio.Threading** (`JoinableTaskFactory`, `AsyncLazy<T>`). | [![NuGet](https://img.shields.io/nuget/vpre/Scarlet.Extensions.Hosting.DirectN.Threading.svg)](https://www.nuget.org/packages/Scarlet.Extensions.Hosting.DirectN.Threading/) |
| **Scarlet.DirectN.Threading** | The UI-thread message queue and `SynchronizationContext` under the host. Installed with the main library. | [![NuGet](https://img.shields.io/nuget/vpre/Scarlet.DirectN.Threading.svg)](https://www.nuget.org/packages/Scarlet.DirectN.Threading/) |

## Quick Start

### 1. Create `Program.cs`

Use `Host.CreateApplicationBuilder` from `Microsoft.Extensions.Hosting`. Everything is registered on `builder.Services`
before calling `Build()`, so the same calls work with any `IHostApplicationBuilder` and inside
`IHostBuilder.ConfigureServices`. There is no application class to write: the host creates DirectN's `Application`.

```csharp
using Microsoft.Extensions.Hosting;
using Scarlet.Extensions.Hosting.DirectN;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDirectN();
builder.Services.AddDirectNLifetime();
builder.Services.AddDirectNComponent<MainWindowComponent>();

using var host = builder.Build();
host.Run();
```

### 2. Create the UI in a component

Components are created with their dependencies from the container, initialized on the UI thread before the message loop
starts, and disposed there (in reverse order) after it ends. The container never disposes them.

```csharp
using DirectN.Extensions.Utilities;
using Scarlet.Extensions.Hosting.DirectN;

public sealed class MainWindowComponent : IDirectNComponent
{
    public void InitializeComponent()
    {
        var window = new Window("My app");
        window.ResizeClient(800, 600);
        window.Center();
        window.Show();
    }
}
```

`AddDirectN` takes options for what WPF's `App` class used to do:

```csharp
builder.Services.AddDirectN(options =>
{
    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;          // tray apps; default OnLastWindowClose
    options.OnStartup = (services, context) => { /* UI thread, before the message loop */ };
    options.OnExit = (services, context) => { /* UI thread, after the message loop */ };
});
```

The DirectN UI thread is created and started once every hosted service has started, the way ASP.NET Core starts serving
requests. If any hosted service fails to start, the UI is never created; failures while creating it (in `OnStartup` or a
component) fail host startup instead of crashing the process. Another hosted service that needs the UI thread can post to
`IDirectNThread.SynchronizationContext` or use `JoinableTaskFactory.SwitchToMainThreadAsync` at any time: work posted
before the UI thread exists runs once it has started. Just don't *wait* for it inside `StartAsync` (awaiting the switch
there, or `JoinableTaskFactory.Run`): the UI thread only starts after every `StartAsync` has returned, so startup would hang.
Do such work from `BackgroundService.ExecuteAsync` or `IHostApplicationLifetime.ApplicationStarted` instead.

Started last, the UI is also stopped first: before any hosted service's `StopAsync`, the host waits until the UI thread has
fully ended (remaining windows closed, `OnExit`, components disposed), up to `HostOptions.ShutdownTimeout`. Application
cleanup can therefore still use other hosted services and every registered service. When the application exits on its own
(last window closed, `Application.Exit()`, `IDirectNThread.HandleApplicationExit()`), the host stops. When the Windows
session ends (log off, shut down, restart, or the Restart Manager closing the application), Windows ends the process right
after announcing it: the application is cleaned up and the host stopped right then, before Windows may end the process.

Known limitations:
- Waiting for the UI thread is bounded by `HostOptions.ShutdownTimeout`, once. Application code that blocks longer (in
  initialization or cleanup) may still run after the host has started disposing services.
- `Environment.Exit` called on the UI thread blocks it for good: the other hosted services are still stopped, but the
  application's cleanup (`OnExit`, components) doesn't run.
- One DirectN host per process: DirectN allows one `Application` per thread and none after its process-wide exit.

`AddDirectNLifetime()` is optional. With any host lifetime, `AddDirectN` stops the host when the DirectN application
exits. `DirectNLifetime` replaces the default `ConsoleLifetime` with one suited to a windowed application: no Ctrl+C
handling or console shutdown hints, status messages (turn them off with
`AddDirectNLifetime(options => options.SuppressStatusMessages = true)`), and a graceful host shutdown when the process exits.

### 3. Update `.csproj`

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <PublishAot>true</PublishAot>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

The manifest should declare `PerMonitorV2` DPI awareness and Common Controls 6 (visual styles, required by task dialogs); see
[the sample's manifest](samples/HostingMinimal/app.manifest).

For the full guide including **components**, **application options**, **thread switching**, **tray icons**, and more, see the **[Wiki](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki)**.

## Samples
1. [HostingMinimal](samples/HostingMinimal): Top-level statements with `Host.CreateApplicationBuilder`, a DirectN window created by a component, and a `BackgroundService` updating it through **Threading**.
2. [HostingTrayIcon](samples/HostingTrayIcon): A windowless tray application (`ShutdownMode.OnExplicitShutdown`) using the **TrayIcon** feature: native menu, balloon tips, and Exit stopping the host.

Both publish with `PublishAot`.

## Documentation

Full documentation is available in the **[Wiki](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki)**:

- [Getting Started](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/3.-Getting-started-%F0%9F%9A%80) — Minimal setup guide
- [Components](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/4.-Components-%F0%9F%A7%A9) — UI-thread objects with constructor injection
- [Application Options](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/5.-Application-options-%E2%9A%99%EF%B8%8F) — Shutdown mode, startup and exit, unhandled exceptions
- [DirectN Lifetime](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/6.-DirectN-Lifetime-%E2%8F%B1%EF%B8%8F) — The host lifetime for windowed applications
- [Threading](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/7.-Threading-extension-%F0%9F%A7%B5) — Switch between UI and background threads
- [TrayIcon](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/8.-TrayIcon-extension-%F0%9F%94%94) — System tray icon support
- [Message Queue](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/9.-Message-queue-%F0%9F%93%AC) — The UI-thread queue and its guarantees
- [Architecture & How It Works](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/10.-Architecture-&-How-it-works-%F0%9F%93%90) — Internal architecture and lifecycle
- [API Reference](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/11.-API-Reference-%F0%9F%93%9A) — Complete API reference
- [FAQ & Troubleshooting](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/13.-FAQ-&-Troubleshooting-%E2%9D%93) — Common questions and error solutions
- [Migrating from Extensions.Hosting.Wpf](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/14.-Migrating-from-Extensions.Hosting.Wpf-%F0%9F%94%84) — API mapping and a before/after example

## License

MIT. `Scarlet.Extensions.Hosting.DirectN.Threading` contains code from
[microsoft/vs-threading](https://github.com/microsoft/vs-threading) (MIT) and the tray icon ports behavior from
[dotnet/winforms](https://github.com/dotnet/winforms) (MIT); see [LICENSE-3RD-PARTY.txt](LICENSE-3RD-PARTY.txt) and
[UPSTREAM.md](UPSTREAM.md).
