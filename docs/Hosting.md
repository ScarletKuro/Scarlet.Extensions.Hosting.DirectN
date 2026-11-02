# Hosting

How `Scarlet.Extensions.Hosting.DirectN` runs a DirectN application inside the Generic Host.

## Lifecycle

1. `AddDirectN()` registers a dedicated STA UI thread (`IDirectNThread.MainThread`). It exists, unstarted, as soon as it is
   resolved, so it can be handed to a `JoinableTaskContext` while services are constructed.
2. The UI thread starts in the host's `StartedAsync`, after every hosted service has started. On it the host creates the
   message queue (`Scarlet.DirectN.Threading`), DirectN's `Application`, runs `DirectNOptions.OnStartup`, then creates and
   initializes the components in registration order.
3. The message loop runs. The first work it processes marks the application as started: host startup completes, and work
   posted before the UI thread existed runs.
4. The application exits when the host stops (`StopApplication()`, `IDirectNThread.HandleApplicationExit()`, process exit with
   `DirectNLifetime`) or on its own (last window closed with `ShutdownMode.OnLastWindowClose`, `Application.Exit()`, a fatal
   DirectN error), which then stops the host.
5. On the UI thread: remaining windows are closed, `DirectNOptions.OnExit` runs, components are disposed in reverse order
   (each one, even if another throws; failures are logged), the queue is closed. The host waits for this before stopping
   the other hosted services.

If a hosted service fails in `StartedAsync` after the UI thread has started, the host is disposed without being stopped.
The application is then still cleaned up before the container disposes the services the components resolved.

## Options

| `DirectNOptions` | |
|---|---|
| `ShutdownMode` | `OnLastWindowClose` (default): exits when the last non-background window closes. DirectN marks windows created after the first one as background windows, so in practice this is the main window. `OnExplicitShutdown`: only exits when asked to, for tray applications. |
| `OnStartup` | `(IServiceProvider, IDirectNContext)` callback on the UI thread before the components are initialized. Exceptions fail host startup. |
| `OnExit` | Callback on the UI thread after the loop has ended and windows were closed, before the components are disposed. |
| `ShowFatalErrorDialog` | Keep DirectN's own fatal-error task dialog. Default `false`: the host logs DirectN errors instead. |

## Unhandled exceptions

Exceptions thrown by work posted to the UI thread raise `IDirectNThread.UnhandledException`. Set `Handled` to keep the
application running; otherwise the exception is logged and terminates the process, like WPF's `DispatcherUnhandledException`.
Exceptions in window procedures are DirectN errors: DirectN exits the message loop, the host logs them and stops (or fails
startup if the application hadn't started yet).

## Posting to the UI thread

`Post` on `IDirectNThread.SynchronizationContext` is usable at any time and never throws (`JoinableTaskFactory` would
terminate the process on a throw):
- before the UI thread exists, work is kept and runs once the application has started;
- while it runs, work is queued to the UI thread, also while a modal loop (menu, message box, dialog, window move) is open;
- after it ended, work is dropped and logged.

`Send` always returns: the work ran (its exception, if any, is rethrown to the caller), or it throws
`InvalidOperationException`. That happens before the UI thread exists (blocking there would deadlock a hosted service's
`StartAsync`, since the UI thread starts after it), when the UI thread ends before running the work, and after it ended.
Like any `Send`, calling it from a thread the UI thread is waiting on deadlocks.

## API

| Member | |
|---|---|
| `services.AddDirectN()` / `AddDirectN(Action<DirectNOptions>)` | Runs the DirectN application in the host. Calling it again only adds configuration. |
| `services.AddDirectNComponent<T>()` / `AddDirectNComponent(Func<IServiceProvider, T>)` | A component created, initialized and disposed on the UI thread; never disposed by the container. |
| `services.AddDirectNLifetime()` / `AddDirectNLifetime(Action<DirectNLifetimeOptions>)` | `DirectNLifetime` instead of `ConsoleLifetime`. |
| `hostBuilder.UseDirectNLifetime()` | The same for `IHostBuilder`. |
| `IDirectNComponent` | `InitializeComponent()` on the UI thread; implement `IDisposable` for cleanup. |
| `IDirectNThread` | `MainThread`, `SynchronizationContext`, `Context`, `HandleApplicationExit()`, `UnhandledException`. |
| `IDirectNContext` | `IsRunning`, `Application` (the DirectN `Application`, for advanced use). |
| `DirectNLifetimeOptions` | `SuppressStatusMessages`. |
