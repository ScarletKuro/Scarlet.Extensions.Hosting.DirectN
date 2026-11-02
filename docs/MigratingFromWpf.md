# Migrating from Extensions.Hosting.Wpf

| Extensions.Hosting.Wpf | Scarlet.Extensions.Hosting.DirectN |
|---|---|
| `services.AddWpf<App>()` | `services.AddDirectN()`. There is no application class: the host creates DirectN's `Application`. |
| `App : IApplicationInitialize` + `AddWpfInitialization<App>()` / `UseWpfInitialization<App>()` | `DirectNOptions.OnStartup`, or an `IDirectNComponent`. |
| `App.OnStartup` / `App.OnExit` overrides | `DirectNOptions.OnStartup` / `DirectNOptions.OnExit`. |
| `ShutdownMode="OnExplicitShutdown"` in `App.xaml` | `DirectNOptions.ShutdownMode = ShutdownMode.OnExplicitShutdown`. |
| `Application.DispatcherUnhandledException` (`e.Handled = true`) | `IDirectNThread.UnhandledException` (`e.Handled = true`). |
| `IWpfComponent` + `Func<IWpfComponent>` registrations | `IDirectNComponent` + `services.AddDirectNComponent<T>()`. |
| `services.AddWpfLifetime()` / `UseWpfLifetime()` + `WpfLifetimeOptions` | `services.AddDirectNLifetime()` / `UseDirectNLifetime()` + `DirectNLifetimeOptions`. |
| `IWpfThread` / `IWpfThread<TApp>` | `IDirectNThread` (`MainThread`, `SynchronizationContext`, `HandleApplicationExit()`). |
| `IWpfContext` (`WpfApplication`, `Dispatcher`, `IsRunning`) | `IDirectNContext` (`Application`, `IsRunning`); post through `IDirectNThread.SynchronizationContext`. |
| `services.AddThreadSwitching()` (Microsoft.VisualStudio.Threading) | `services.AddThreadSwitching()`; replace `using Microsoft.VisualStudio.Threading;` with `using Scarlet.Extensions.Hosting.DirectN.Threading;`. |
| `services.AddWpfTrayIcon<TrayIcon>()` + WinForms `NotifyIcon` | `services.AddDirectNTrayIcon<TrayIcon>()` + `TrayNotifyIcon` ([docs](TrayIcon.md)). |
| `ContextMenuStrip` / `ToolStripMenuItem` | `TrayMenu` / `TrayMenuItem`. |
| Reflection call of `NotifyIcon.ShowContextMenu` on left click | `ShowContextMenuOnLeftClick = true`, or `TrayNotifyIcon.ShowContextMenu()`. |
| `System.Windows.Forms.Application.ProductVersion` | `ApplicationInfo.ProductVersion`. |
| `Resources.MyIcon` (`System.Drawing.Icon`) | `TrayIconImage.FromApplicationIcon()` / `FromStream(...)` / `FromFile(...)`. |
| ReactiveUI `DispatcherScheduler(Dispatcher)` | `new SynchronizationContextScheduler(directNThread.SynchronizationContext)`. |

Not ported: the ViewModelLocator APIs and `CheckForInvalidConstructorConfiguration` (XAML-specific), and the Bootstrap package.

A WPF app before and after:

```csharp
// Before
services.AddWpf<App>();
services.AddWpfInitialization<App>();
services.AddThreadSwitching();
services.AddWpfTrayIcon<TrayIcon>();

// After
services.AddDirectN(options =>
{
    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    options.OnStartup = (sp, context) => RxApp.MainThreadScheduler =
        new SynchronizationContextScheduler(sp.GetRequiredService<IDirectNThread>().SynchronizationContext);
});
services.AddThreadSwitching();
services.AddDirectNTrayIcon<TrayIcon>();
```
