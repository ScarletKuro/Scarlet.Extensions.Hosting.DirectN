# Scarlet.Extensions.Hosting.DirectN — Implementation Plan

> **Status (2026-11-02): implemented.** Deviations from the plan below: the tray icon class is `TrayNotifyIcon` (DirectN.Extensions.Utilities already has a `NotifyIcon`); `AwaitExtensions` (`await TaskScheduler.Default`) stays public because Kiisu/Liisu use it; the ScenarioApp doubles as the AOT smoke test; there is no `samples/HostingBlazorWebView` yet, because `Scarlet.AspNetCore.Components.WebView.Win32Aot` doesn't consume `Scarlet.DirectN.Threading` yet (out of scope for this repo).

Goal: host a DirectNAot (Win32 / Direct2D, Native AOT) desktop app with the same
`Microsoft.Extensions.Hosting` builder model as `Extensions.Hosting.Wpf`, plus
`JoinableTaskFactory` style thread switching and `AsyncLazy<T>`, without WPF and fully
Native AOT compatible. First consumers: Kiisu (`WinPOS.Kiisu.UI`) and Liisu (`Liisu.DesktopApp`).

Sources:
- Ported logic: `..\Microsoft.Extensions.Hosting.Wpf` (v5.0.0 + #22, commit `ac01e4f`).
- DirectNAot reviewed at `smourier/DirectNAot@0f54981` (2026-10-29): `DirectN.Extensions/Utilities/`
  (`Application`, `ApplicationWithScheduler`, `Window`, `WindowScheduler`, `WindowSynchronizationContext`,
  `NotifyIcon`, `Icon`) and `Samples/DirectN.Samples.HelloWorld`.
- `..\Scarlet.AspNetCore.Components.WebView` (`Win32Aot/`): BlazorWebView on DirectN; source of the UI-thread queue (2.3).

---

## 0. Decisions (fixed up front)

| Topic | Decision |
|---|---|
| Target framework | `net10.0-windows` only |
| AOT | `<IsAotCompatible>true</IsAotCompatible>` on every library project; zero warnings allowed (`TreatWarningsAsErrors`) |
| Package prefix | `Scarlet.` (reserved) |
| Relationship to the WPF repo | Standalone port (copy + adapt) |
| Application object | **No `TApplication` generic and no user `App` class.** The host creates DirectN's own `DirectN.Extensions.Utilities.Application` on the UI thread and exposes it read-only. User code hooks in through components and options (see 2.1) |
| UI thread marshalling | Reuse `Win32MessageQueue` + `Win32SynchronizationContext` from `Scarlet.AspNetCore.Components.WebView.Win32Aot` (message-only window, already AOT-tested), moved into a small shared package so the host and BlazorWebView share **one** queue per UI thread. DirectN's `WindowSynchronizationContext` and `ApplicationScheduler` are not used (see 2.3) |
| DirectNAot version | Same as `Scarlet.AspNetCore.Components.WebView.Win32Aot` (currently `DirectNAot` / `DirectNAot.Extensions` 1.7.1); bump both repos together |
| Win32 interop | DirectNAot's generated `Functions`/`Constants`/structs. No CsWin32, no own `DllImport`s unless DirectNAot lacks something |
| vs-threading | Extracted source, renamed namespace, no reference to `Microsoft.VisualStudio.Threading` |
| Upstream pin | One vs-threading tag (start from `v17.12.19`, the version the WPF repo uses, or the latest 17.x tag); record the commit SHA in `UPSTREAM.md` |

### What Kiisu / Liisu actually use from the WPF host (drives the API)

Searched both apps (`src/WinPOS.Kiisu.UI`, `src/Liisu.DesktopApp`):

| Used | Where | DirectN equivalent |
|---|---|---|
| `AddWpf<App>()`, `AddWpfInitialization<App>()` | both entry points | `AddDirectN(options)` (non-generic) |
| `App : IApplicationInitialize` → `Initialize()` (ReactiveUI setup, `RxApp.MainThreadScheduler` from the Dispatcher) | both `App.xaml.cs` | an `IDirectNComponent`, or an `OnStartup` callback; Rx gets `new SynchronizationContextScheduler(thread.SynchronizationContext)` |
| `App.OnStartup` / `OnExit`: hook `DispatcherUnhandledException` (log + MessageBox, `Handled = true`), `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` | both | `IDirectNThread.UnhandledException` event with `Handled`; the AppDomain/TaskScheduler hooks stay in user code |
| `ShutdownMode="OnExplicitShutdown"` | both `App.xaml` | `DirectNOptions.ShutdownMode = ShutdownMode.OnExplicitShutdown` |
| `AddThreadSwitching()` (non-generic) + `JoinableTaskFactory.SwitchToMainThreadAsync()` | ~15 call sites in UI services | same API from `Scarlet.Extensions.Hosting.DirectN.Threading` |
| `AsyncLazy<T>(factory, joinableTaskFactory)` | `CoreWebView2EnvironmentProvider` | same |
| `new JoinableTaskFactory(new JoinableTaskContext())` (no main thread, background-service mode) | Kiisu `EntryPoint.cs:270` | must keep working: context without a main thread (Kiisu references the Threading package in both modes; pulling in the host + DirectNAot there is accepted) |
| `AddWpfTrayIcon<TrayIcon>()`, `IWpfThread.HandleApplicationExit()` | both `TrayIcon.cs` | `AddDirectNTrayIcon<T>()`, `IDirectNThread.HandleApplicationExit()` |
| `WpfApplication`, `IWpfContext`, `Dispatcher` from the host | **not used** | `IDirectNContext.Application` exposed for advanced use only |
| `System.Windows.Forms.Application.ProductVersion` | Kiisu `EntryPoint.cs`, both tray icons | `ApplicationInfo.ProductVersion` (TrayIcon package) |

Liisu's Blazor Hybrid UI (`AddWpfBlazorWebView`, WebView2) moves to the existing
`Scarlet.AspNetCore.Components.WebView.Win32Aot` (`..\Scarlet.AspNetCore.Components.WebView`, DirectN based,
AOT compatible). This library only has to host it well: shared UI-thread queue (2.3), and a sample (Phase 5).

Out of scope here but flagged for the app migrations:
- Kiisu's views are WPF/XAML + ReactiveUI; those are rewritten by the app migration, not by this library.
- `VirtualKeyboard.TabTip`/`VirtualKeyboard.Wpf` use the WPF `Dispatcher` and need porting to a
  `SynchronizationContext`-based scheduler during the app migration.

### Packages / projects

| Project | Purpose | Depends on |
|---|---|---|
| `Scarlet.DirectN.Threading` | `Win32MessageQueue` + `Win32SynchronizationContext` moved out of the WebView repo and made public (see 2.3). Also consumed by `Scarlet.AspNetCore.Components.WebView.Win32Aot` | DirectNAot (its generated interop replaces the WebView repo's own `LibraryImport`s; both consumers reference DirectNAot already, so it costs them nothing) |
| `Scarlet.Extensions.Hosting.DirectN` | Host integration: UI thread, lifetime, hosted service, components | `Microsoft.Extensions.Hosting` (10.x), DirectNAot, `Scarlet.DirectN.Threading` |
| `Scarlet.Extensions.Hosting.DirectN.Threading` | The extracted `JoinableTaskContext`, `JoinableTaskFactory`, `JoinableTask(<T>)`, `JoinableTaskCollection`, `AsyncLazy<T>` (Phase 1), plus `AddThreadSwitching()` registering them bound to the DirectN UI thread (Phase 3). Deliberately not a separate general-purpose package: only meant for this stack, fewer packages to publish | `Scarlet.Extensions.Hosting.DirectN` |
| `Scarlet.Extensions.Hosting.DirectN.TrayIcon` | `AddDirectNTrayIcon<T>()` hosting hook + a full-featured `NotifyIcon` with WinForms behaviour and a native menu | `Scarlet.Extensions.Hosting.DirectN` |

### Not ported (out of scope)

- `TApplication` / `IApplicationInitializeComponent` / `IApplicationInitialize`: no app class, see 2.1.
- `Extensions.Hosting.Wpf.Bootstrap` (`IBootstrap`): no Bootstrap package in this repo.
- `ApplicationExtensions.CheckForInvalidConstructorConfiguration`: WPF-specific.
- ViewModelLocator (`AddWpfViewModelLocator`, `UseWpfViewModelLocator`, `AbstractViewModelLocatorHost`,
  `IViewModelLocatorInitialization`, `WpfInitializationActions.ViewModelLocator`): built for XAML resources.

### Repo layout

```
Scarlet.Extensions.Hosting.DirectN/
  Directory.Build.props          # LangVersion, Nullable, net10.0-windows, IsAotCompatible, analyzers, signing
  Directory.Packages.props       # central package management
  Scarlet.Extensions.Hosting.DirectN.slnx
  LICENSE                        # MIT
  LICENSE-3RD-PARTY.txt          # vs-threading + dotnet/winforms MIT notices
  UPSTREAM.md                    # vs-threading tag/SHA, copied files, local modifications
  src/
    Scarlet.DirectN.Threading/
    Scarlet.Extensions.Hosting.DirectN/
    Scarlet.Extensions.Hosting.DirectN.Threading/   # JTF/AsyncLazy extract in its own folder (Joinable/) + AddThreadSwitching
    Scarlet.Extensions.Hosting.DirectN.TrayIcon/
  tests/
    Scarlet.DirectN.Threading.Tests/
    Scarlet.Extensions.Hosting.DirectN.Tests/
    Scarlet.Extensions.Hosting.DirectN.Threading.Tests/
    Scarlet.Extensions.Hosting.DirectN.TrayIcon.Tests/
    Scarlet.Extensions.Hosting.DirectN.ScenarioApp/  # exe driven by tests through exit codes (one DirectN Application per process)
    Scarlet.Extensions.Hosting.DirectN.AotSmoke/     # PublishAot=true, CI publishes + runs it
  samples/
    HostingMinimal/
    HostingTrayIcon/
  docs/
  .github/workflows/             # build, test, AOT publish, pack
```

---

## Phase 0 — Spikes (½ day)

Most DirectN questions are already answered (see 2.2–2.3 and 4.1). What's left:

1. **Is extraction actually needed?** A throwaway `net10.0` (no `-windows`) class library referencing
   `Microsoft.VisualStudio.Threading`, using `JoinableTaskFactory` + `AsyncLazy<T>`, referenced from a `PublishAot=true` exe.
   Record the package asset picked, whether WPF/WindowsBase comes in, and the IL2xxx/IL3xxx warnings.
   If it's clean (or nearly), referencing the package from a non-`-windows` library is the cheaper route.
2. **Hello-AOT host:** `Host.CreateApplicationBuilder()` + DirectN `Application` + one `Window`, `PublishAot=true`,
   no warnings (configuration binding source generator on).
3. **Shared queue location:** confirm moving `Win32MessageQueue`/`Win32SynchronizationContext` into
   `Scarlet.DirectN.Threading` (this repo) and switching the WebView repo to consume it (see 2.3).

Exit criteria: notes in `docs/spikes.md`, decision on extraction.

---

## Phase 1 — JTF + AsyncLazy extract into `Scarlet.Extensions.Hosting.DirectN.Threading` (2–4 days)

Lives in the `Joinable/` folder of the Threading project, kept separate from the hosting glue so upstream diffs stay easy.

### 1.1 Copy

From `microsoft/vs-threading` at the pinned tag, `src/Microsoft.VisualStudio.Threading/`, copy the minimal
closure needed for `JoinableTaskFactory` + `AsyncLazy<T>`. Starting list (let the compiler find the rest):

- `JoinableTaskContext`, `JoinableTaskContextNode`, `JoinableTaskFactory`, `JoinableTask`, `JoinableTask`1`,
  `JoinableTask.ExecutionQueue`, `JoinableTaskCollection`, `JoinableTaskDependencyGraph`,
  `JoinableTaskSynchronizationContext`, `JoinableTaskCreationOptions`, `JoinableTaskContextException`
- `AsyncLazy`1`
- Helpers: `AsyncManualResetEvent`, `AsyncQueue`1`, `WeakKeyDictionary`, `ListOfOftenOne`,
  `SpecializedSyncContext`, `ThreadingTools`, `TplExtensions`, the used parts of `AwaitExtensions`,
  `HashSetPool`, `Strings.resx`
- `JoinableTaskEventSource`: keep (EventSource is AOT-safe) or stub to no-ops
- `Microsoft.VisualStudio.Validation` (`Requires`, `Assumes`, `Verify`, `Report`): inline the used methods as `internal`

Don't copy: anything with `Dispatcher`/`WindowsBase`, COM/`CoWait`, `AsyncReaderWriterLock`,
`ReentrantSemaphore`, `AsyncSemaphore` (add later on demand).

### 1.2 Adapt

- Namespace `Microsoft.VisualStudio.Threading` → `Scarlet.Extensions.Hosting.DirectN.Threading`. Type and member names stay identical,
  so migrating Kiisu/Liisu is a `using` change.
- Public surface:
  - `new JoinableTaskContext(Thread? mainThread = null, SynchronizationContext? synchronizationContext = null)`
    (the no-main-thread form must keep working for Kiisu's background-service mode)
  - `JoinableTaskFactory`: `Run`, `RunAsync`, `SwitchToMainThreadAsync`; `JoinableTaskContext.IsOnMainThread`
  - `new AsyncLazy<T>(Func<Task<T>> valueFactory, JoinableTaskFactory? joinableTaskFactory = null)`,
    `GetValueAsync`, `GetValue`, `IsValueCreated`, `IsValueFactoryCompleted`, `SuppressRelevance`
  - everything else `internal` unless needed
- Remove `#if NETFRAMEWORK` / older-TFM branches.
- Fix every IL/AOT warning (expected spots: diagnostics/hang reports using `Delegate.Method`, `StackTrace`).
- Keep upstream file headers; add `// Modified for Scarlet.Extensions.Hosting.DirectN.Threading` where edited.
- Package ships `LICENSE-3RD-PARTY.txt`; description/README say it contains a trimmed, AOT-compatible extract of
  `Microsoft.VisualStudio.Threading` (JoinableTaskFactory, AsyncLazy), with the upstream tag, not affiliated with Microsoft.
- `LICENSE-3RD-PARTY.txt` entry and `UPSTREAM.md` (tag, SHA, file list, modifications, diff script).

### 1.3 Analyzers

The VSTHRD analyzers match the `Microsoft.VisualStudio.Threading` namespace and won't fire on the renamed types.
Accept that for v1. Optional later: fork the most useful rules (VSTHRD001/002/003/010/100/103/110/200).

### 1.4 Tests

- Port the relevant upstream tests (`JoinableTaskTests`, `JoinableTaskFactoryTests`, `AsyncLazyTests`,
  `JoinableTaskContextTests`) with their single-threaded `SynchronizationContext` harness.
- AOT smoke: `Run` → `SwitchToMainThreadAsync` → `AsyncLazy` on a message-pump sync context; exe must exit 0.

Exit criteria: zero warnings with `IsAotCompatible`, ported tests green, AOT smoke runs.

---

## Phase 2 — `Scarlet.Extensions.Hosting.DirectN` (3–5 days)

### 2.1 Public API

```csharp
var builder = Host.CreateApplicationBuilder(args);       // IHostApplicationBuilder (AOT path); IHostBuilder overloads too

builder.Services.AddDirectN(o =>
{
    o.ShutdownMode = ShutdownMode.OnExplicitShutdown;    // or OnLastWindowClose (DirectN's default behaviour)
    o.OnStartup = (sp, context) => { /* UI thread, before the loop: e.g. RxApp.MainThreadScheduler */ };
    o.OnExit    = (sp, context) => { /* UI thread, after the loop */ };
});
builder.Services.AddDirectNLifetime();                    // + Action<DirectNLifetimeOptions>
builder.Services.AddDirectNComponent<MainWindowComponent>(); // + Func<IServiceProvider, T> overload

using var host = builder.Build();
await host.RunAsync();
```

```csharp
public interface IDirectNThread
{
    Thread MainThread { get; }                            // exists before the UI thread starts (JTF needs it)
    SynchronizationContext SynchronizationContext { get; }// usable before start: posts are queued (#22 behaviour)
    IDirectNContext Context { get; }
    void HandleApplicationExit();                         // any thread; exits the loop, host stops
    event EventHandler<DirectNUnhandledExceptionEventArgs>? UnhandledException; // UI thread, has Handled
}

public interface IDirectNContext
{
    bool IsRunning { get; }
    DirectN.Extensions.Utilities.Application? Application { get; } // advanced use only (windows list, accelerators)
}

public interface IDirectNComponent { void InitializeComponent(); } // UI thread; disposed on UI thread if IDisposable
```

Mapping from the WPF package:

| WPF package | DirectN package |
|---|---|
| `AddWpf<TApp>()` / `AddWpf<TApp>(Func<…>)` | `AddDirectN()` / `AddDirectN(Action<DirectNOptions>)` |
| `IApplicationInitialize.Initialize` + `AddWpfInitialization` / `UseWpfInitialization` | `DirectNOptions.OnStartup` or an `IDirectNComponent` |
| `App.OnExit` | `DirectNOptions.OnExit` / component `Dispose` |
| `Application.DispatcherUnhandledException` | `IDirectNThread.UnhandledException` |
| `ShutdownMode` | `DirectNOptions.ShutdownMode` |
| `IWpfComponent` + `Func<IWpfComponent>` registrations | `IDirectNComponent` + `AddDirectNComponent<T>()` |
| `AddWpfLifetime` / `UseWpfLifetime` + `WpfLifetimeOptions` | `AddDirectNLifetime` / `UseDirectNLifetime` + `DirectNLifetimeOptions` |
| `IWpfThread` / `IWpfContext` (+ generic variants) | `IDirectNThread` / `IDirectNContext` (non-generic only) |

### 2.2 How DirectN's `Application` is used

Facts from `Application.cs`:
- One instance per thread, registered in a static dictionary; after `Application.AllExit()` no new instance
  can be created in the process. **One DirectN host per process** (same as WPF), so tests use the ScenarioApp exe.
- `Run()` = `GetMessage` loop; returns `ExitLoopReason`. Doesn't start at all if `Application.HasErrors` is already true.
- `Exit()` raises `ApplicationExiting` (cancellable) / `ApplicationExit`, then `PostQuitMessage` (UI thread) or
  `PostThreadMessage(WM_QUIT)` (other threads).
- `ExitOnLastWindowRemoved` (instance) and `ExitAllOnLastWindowRemoved` (static) default to `true`:
  closing the last non-background window exits.
- Static constructor hooks `AppDomain.UnhandledException` → `AddError` (posts `WM_APP_QUIT`) and shows a fatal-error
  TaskDialog unless `ShowingFatalError` is cancelled or `ShowFatalErrorFunc` is set.

What the host does with it:
- Creates the `Application` on the UI thread. `ShutdownMode.OnExplicitShutdown` → `ExitOnLastWindowRemoved = false`
  and `Application.ExitAllOnLastWindowRemoved = false`.
- `HandleApplicationExit()` always marshals to the UI thread first and calls `Exit()` there, so the exit is
  `PostQuitMessage` (survives modal loops) instead of a thread message (lost in modal loops).
- Hooks `Application.ShowingFatalError` → logs through `ILogger`, raises `UnhandledException`; option
  `DirectNOptions.ShowDirectNFatalErrorDialog` (default `false`) decides whether DirectN's TaskDialog still shows.
- `Run()` returning (any `ExitLoopReason`) = application exit → host stops (via `DirectNLifetime`), exit code preserved.

### 2.3 `DirectNSynchronizationContext`

Why not DirectN's own:
- `WindowSynchronizationContext.Post` goes through the first window's `WindowScheduler` and **silently drops the
  callback when no window exists** (`GetWindow()?.RunTaskOnUIThread`). Kiisu/Liisu are tray apps that start with no window.
- `ApplicationWithScheduler`/`ApplicationScheduler` only drains inside `Application.Run`'s `GetMessage`
  (`MsgWaitForMultipleObjectsEx` on its events). Nested modal loops (`TrackPopupMenuEx`, `MessageBox`, `TaskDialog`,
  window move/resize, DirectN's `RunModalLoop`) don't call it, so `SwitchToMainThreadAsync` would stall while a
  tray menu or message box is open. The WPF Dispatcher doesn't have this problem because it posts to a window.

Design: **reuse what `Scarlet.AspNetCore.Components.WebView.Win32Aot` already has.** Its internal
`Win32MessageQueue` (242 lines) + `Win32SynchronizationContext` (61 lines) are exactly this design and already
AOT-tested: a per-thread message-only window (`HWND_MESSAGE`, dispatched by every modal loop), `ConcurrentQueue`,
a coalesced wake message (`RegisterWindowMessageW`, so no clash with DirectN's `WM_APP_*` IDs), max 256 items per wake-up
so input/paint aren't starved, `Send` inline on the UI thread or post + wait, and an `UnhandledException` event with `Handled`.

Move it into `Scarlet.DirectN.Threading` (in this repo) rather than copying it:
- `BlazorWebView` calls `Win32MessageQueue.GetOrCreateForCurrentThread()`. If the host had its own context, the UI thread
  would have two queues with no ordering between host posts and Blazor dispatcher posts, two exception paths, and two
  `SynchronizationContext`s depending on who resumed an `await`. One shared per-thread queue avoids all of that.
- Changes when moving: make them public as `DirectNMessageQueue` / `DirectNSynchronizationContext` (namespace
  `Scarlet.DirectN.Threading`, matching the package); swap the own `LibraryImport`s for DirectNAot's `Functions`;
  replace `BlazorWebViewUnhandledExceptionEventArgs` with a neutral `Win32UnhandledExceptionEventArgs`; keep
  `RethrowAsUnhandled` as the fallback when nobody handles. Then the WebView repo references the package and deletes its copies
  (its `BlazorWebViewUnhandledExceptionEventArgs` can stay as its public API, mapped from the shared event).
- Fallback if you'd rather not touch the WebView repo now: copy the two files into the hosting package (internal) and accept
  two queues on the UI thread until a later release unifies them.

What the host adds on top:
- Calls `Win32MessageQueue.GetOrCreateForCurrentThread()` first thing on the UI thread and installs its
  `SynchronizationContext` as `SynchronizationContext.Current` for the whole loop.
- Posts made before the UI thread exists sit in the pre-start queue (`IUiThreadPoster`, #22) and are flushed into the queue after creation.
- Subscribes to `UnhandledException`: raises `IDirectNThread.UnhandledException`; if not handled → log,
  `Application.AddError`, host stops (WPF parity: an unhandled dispatcher exception ends the app).

### 2.4 Port, file by file

| WPF source | Action |
|---|---|
| `GenericHost/WpfLifetime.cs` | Port nearly verbatim → `DirectNLifetime` (host stop ↔ UI exit, exit code preservation) |
| `GenericHost/WpfLifetimeOptions.cs` | Rename |
| `GenericHost/WpfHostedService.cs` | Port; replace `Dispatcher.HasShutdownStarted` / `InvokeAsync(Shutdown)` / `DispatcherOperationStatus.Aborted` with posting `Exit()` through our context and awaiting UI thread end |
| `Internal/WpfStartupGuard.cs` | Port |
| `Internal/WpfInitialization*.cs`, `WpfInitializationValidator.cs` | Replace with `DirectNOptions.OnStartup`/`OnExit`; keep only the "configured after start" / "registered twice" validations that still apply |
| `Core/IWpfComponent.cs` + `InitializeWpfComponents` | Port as `IDirectNComponent`: created from `Func<IDirectNComponent>` registrations, initialized on the UI thread in registration order before the loop, disposed on the UI thread in reverse order (`DisposableList`) after the loop, never by the container |
| `Internal/IUiThreadPoster.cs` + pre-start queue | Port as is (#22) |
| `Internal/WpfThread.cs` | **Rewrite** as `DirectNThread`: STA thread → message-only window + `DirectNSynchronizationContext` → `new Application()` → `OnStartup` → components → post a "started" marker → `app.Run()`. The marker running inside the loop is "started" (no Dispatcher hooks). Keep the guarantees: startup failures fail `StartAsync`; `StopAndWait` waits for the UI thread (bounded by `HostOptions.ShutdownTimeout`); a stopped thread never starts the loop |
| `Internal/WpfContext.cs` | Rewrite as `DirectNContext` |
| `Internal/DisposableList.cs`, `ThrowHelper.cs`, `LoggingExtensions.cs` | Copy (`[LoggerMessage]` source generator) |
| `ServiceCollectionExtensions.cs`, `WpfHostingExtensions.cs` | Port without generics; duplicate-registration checks stay; add `IHostApplicationBuilder` overloads. `ActivatorUtilities.CreateInstance<T>` for components needs `[DynamicallyAccessedMembers(PublicConstructors)]` on `T` |

### 2.5 Tests

- Port lifecycle scenarios from the WPF suite: start/stop ordering, startup failure fails `StartAsync`, app exit stops
  the host, host stop exits the app, exit codes, pre-start posts (#22), duplicate registration errors, components
  initialized/disposed on the UI thread in order.
- New: `OnExplicitShutdown` keeps running with zero windows; `OnLastWindowClose` exits when the last window closes;
  posts are processed while a `MessageBox`/`TrackPopupMenuEx` is open; unhandled exception in a posted callback
  (handled vs not handled).
- Each scenario runs in a ScenarioApp process (one DirectN `Application` per process).

Exit criteria: lifecycle tests green; sample runs from `dotnet run` and as an AOT publish.

---

## Phase 3 — `Scarlet.Extensions.Hosting.DirectN.Threading` (1 day)

```csharp
services.AddThreadSwitching();
// JoinableTaskContext = new(directNThread.MainThread, new DirectNThreadSynchronizationContext(directNThread))
// JoinableTaskFactory  (singleton)
```

- `DirectNThreadSynchronizationContext` forwards to `IDirectNThread` through `IUiThreadPoster`, so posts before startup are queued, not lost.
- Tests: `SwitchToMainThreadAsync` before/after start and while a modal loop is open; `JoinableTaskFactory.Run` on the UI
  thread doesn't deadlock; `AsyncLazy` with the JTF from background and UI threads.

---

## Phase 4 — `Scarlet.Extensions.Hosting.DirectN.TrayIcon` (3–4 days)

### 4.1 Approach

DirectNAot has a `NotifyIcon` (342 lines), but it's minimal for this use:
- It only raises `MenuOpening(POINT)` on left **and** right button up; no click/double-click/mouse events,
  no balloon events, no menu.
- It sets `NOTIFYICON_VERSION_4` but parses callbacks the legacy way (whole `lParam` as the message).
- Tooltip is truncated at 128 chars (the buffer holds 127 + null).
- Its hidden window has `WS_EX_NOACTIVATE`, which fights the `SetForegroundWindow` needed for menus.

So write our own `NotifyIcon`, porting the **behaviour** of WinForms `NotifyIcon.cs` (dotnet/winforms, MIT,
production tested), on DirectNAot's interop (`Functions.Shell_NotifyIconW`, `NOTIFYICONDATAW`, menu APIs) and
DirectN's `Icon` class. A literal WinForms port isn't possible: `ContextMenuStrip` drags in ToolStrip/GDI+, which
isn't AOT friendly. Replacements:

| WinForms | Replacement |
|---|---|
| `NativeWindow` (hidden window for callbacks) | Own hidden **top-level** window (never shown). Not `HWND_MESSAGE`: message-only windows don't get the broadcast `TaskbarCreated` |
| `ContextMenuStrip` | Native popup menu (`CreatePopupMenu`/`InsertMenuItemW`/`TrackPopupMenuEx`), rebuilt from the managed model on each open. Native Windows 11 look, dark mode follows the shell, mnemonics and accessibility for free |
| `System.Drawing.Icon` | `TrayIconImage` (owns an `HICON`; wraps or uses DirectN's `Icon`) |

Kept from WinForms: add/modify/delete, `Visible` toggling, 127-char tooltip check, re-add on `TaskbarCreated`,
the mouse event set and ordering, balloon tips + events, and the menu sequence
`SetForegroundWindow` → `TrackPopupMenuEx` → `PostMessage(WM_NULL)` so the menu closes on an outside click.

Deliberate differences: `NOTIFYICON_VERSION_4` parsed correctly (keyboard `NIN_SELECT`/`NIN_KEYSELECT`, Shift+F10/Apps →
`WM_CONTEXTMENU`, correct anchor point); public `ShowContextMenu()` and `ShowContextMenuOnLeftClick` instead of the
reflection hack on WinForms' private `ShowContextMenu` (which wouldn't work under AOT); icon re-applied on DPI change
(`LoadIconMetric(LIM_SMALL)` for resource icons).

Worth offering the fixes above to DirectNAot upstream afterwards; not a dependency for us.

### 4.2 API

```csharp
public interface ITrayIcon : IDirectNComponent, IDisposable { }
services.AddDirectNTrayIcon<TTrayIcon>();                         // + Func<IServiceProvider, TTrayIcon> overload

// UI thread only; throws InvalidOperationException from another thread
public sealed class NotifyIcon : IDisposable
{
    public TrayIconImage? Icon { get; set; }
    public string? Text { get; set; }                             // tooltip, max 127 chars
    public bool Visible { get; set; }
    public TrayMenu? ContextMenu { get; set; }
    public bool ShowContextMenuOnLeftClick { get; set; }

    public void ShowContextMenu();
    public void ShowBalloonTip(string title, string text, TrayToolTipIcon icon = TrayToolTipIcon.None);

    public event EventHandler? Click, DoubleClick;
    public event EventHandler<TrayMouseEventArgs>? MouseClick, MouseDoubleClick, MouseDown, MouseUp, MouseMove;
    public event EventHandler? BalloonTipShown, BalloonTipClicked, BalloonTipClosed;
}

public sealed class TrayMenu
{
    public IList<TrayMenuItem> Items { get; }
    public event EventHandler? Opening;                           // update items just before the menu shows
}

public class TrayMenuItem
{
    public TrayMenuItem(string text, EventHandler? click = null);
    public string Text { get; set; }                              // '&' mnemonics
    public bool Enabled { get; set; }
    public bool Checked { get; set; }
    public bool IsDefault { get; set; }                           // bold, SetMenuDefaultItem
    public IList<TrayMenuItem> Items { get; }                     // submenu
    public event EventHandler? Click;
    public static TrayMenuItem Separator();
}

public sealed class TrayIconImage : IDisposable
{
    public static TrayIconImage FromApplicationIcon();            // the exe's <ApplicationIcon>
    public static TrayIconImage FromResource(int resourceId);
    public static TrayIconImage FromFile(string icoPath);
    public static TrayIconImage FromStream(Stream ico);           // embedded .ico, best size for the DPI
    public static TrayIconImage FromHandle(nint hIcon, bool ownsHandle);
}

public static class ApplicationInfo { public static string ProductVersion { get; } } // entry assembly AssemblyInformationalVersion
```

Kiisu's tray icon becomes:

```csharp
public sealed class TrayIcon(IDirectNThread thread) : ITrayIcon
{
    private NotifyIcon? _notifyIcon;

    public void InitializeComponent()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconImage.FromApplicationIcon(),
            Text = $"Kiisu for WinPos {ApplicationInfo.ProductVersion}",
            ShowContextMenuOnLeftClick = true,
            ContextMenu = new TrayMenu
            {
                Items =
                {
                    new TrayMenuItem(ApplicationInfo.ProductVersion) { Enabled = false },
                    new TrayMenuItem("E&xit", (_, _) => thread.HandleApplicationExit()),
                },
            },
            Visible = true,
        };
    }

    public void Dispose() => _notifyIcon?.Dispose();              // UI thread, removes the icon
}
```

### 4.3 Tests

- Unit: menu model → `HMENU` (IDs, checked, disabled, default, submenus, separators), command ID → click dispatch,
  tooltip length check, ICO stream size selection.
- Message handling: send `WM_LBUTTONUP`, `NIN_SELECT`, `WM_CONTEXTMENU`, `NIN_BALLOONUSERCLICK`, `TaskbarCreated`
  directly to the hidden window and assert events and order.
- Manual checklist (README): Explorer restart re-adds the icon, menu closes on outside click, keyboard access
  (Win+B → arrows → Enter / Shift+F10), DPI change across monitors, dispose removes the icon (no ghost icon).

---

## Phase 5 — Samples, docs, CI, release (2 days)

- `samples/HostingMinimal`: `Host.CreateApplicationBuilder` + `AddDirectN()` + `AddDirectNLifetime()` +
  `AddThreadSwitching()` + a component that opens a DirectN `Window` + an `IHostedService` doing
  `await jtf.SwitchToMainThreadAsync()` and updating the window.
- `samples/HostingTrayIcon`: `OnExplicitShutdown`, the tray icon above (left click opens the menu, Exit stops the host).
- `samples/HostingBlazorWebView`: the Liisu shape. Host + `Scarlet.AspNetCore.Components.WebView.Win32Aot` window opened from a
  service via `SwitchToMainThreadAsync`, `AsyncLazy<CoreWebView2Environment>` shared environment, tray icon; AOT published in CI.
- `README.md`, `docs/Threading.md` (differences from vs-threading, no analyzers), `docs/TrayIcon.md`,
  `docs/MigratingFromWpf.md` (the mapping tables above), `CHANGELOG.md`.
- GitHub Actions (based on the WPF repo's workflows): build → test → AOT publish of AotSmoke + samples,
  fail on any IL warning → run AotSmoke → pack → push to NuGet on tag.
- SourceLink, `snupkg`, deterministic CI build, strong naming (new `.snk`), Renovate.

---

## Phase 6 — Optional later work

- Shared core with the WPF repo (`Scarlet.Extensions.Hosting.UI`) once the port has settled.
- Forked VSTHRD analyzers for the renamed namespace.
- Upstream the `NotifyIcon` fixes to DirectNAot.
- Track upstream vs-threading releases; diff the copied files on each upgrade (script in `UPSTREAM.md`).

---

## Risks

| Risk | Mitigation |
|---|---|
| JTF deadlock/hang fixes upstream are missed | Pin tag, keep copied files close to upstream, scripted diff on upgrade |
| Losing VSTHRD analyzers | Document; optional analyzer fork |
| DirectN's static `Application` state (fatal-error dialog, `AllExit`, one app per thread, `HasErrors` blocks `Run`) surprises the host | Wrap it explicitly (2.2); ScenarioApp tests cover exit/error paths; DirectNAot pinned to a version |
| DirectNAot API changes (it's moving fast) | Pin package version; we use only `Application`, `Icon` and generated interop |
| Posts stalling in modal loops | Message-window based queue (2.3); test with menu/MessageBox open |
| Host and BlazorWebView drifting apart (queue, DirectNAot version) | Shared `Scarlet.DirectN.Threading`; same DirectNAot version in both repos; Blazor sample in CI |
| Hidden reflection in `Microsoft.Extensions.*` config binding | `Host.CreateApplicationBuilder`, binding source generator, AOT publish in CI |

## Rough estimate

Phase 0: ½ d · Phase 1: 2–4 d · Phase 2: 3–5 d · Phase 3: 1 d · Phase 4: 3–4 d · Phase 5: 2 d → **~2½–3 weeks**.
