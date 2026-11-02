# Scarlet.DirectN.Threading

The UI-thread work queue and `SynchronizationContext` for DirectN (Win32) apps, Native AOT compatible. Used by
[Scarlet.Extensions.Hosting.DirectN](../README.md) and meant to be shared with
`Scarlet.AspNetCore.Components.WebView.Win32Aot`, so a UI thread has **one** queue: one ordering, one exception path and one
`SynchronizationContext` for the host, the BlazorWebView and the app.

```csharp
// On the UI thread (it must run a message loop, e.g. DirectN's Application.Run()):
var queue = DirectNMessageQueue.GetOrCreateForCurrentThread();
SynchronizationContext.SetSynchronizationContext(queue.SynchronizationContext);
queue.UnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };

// From any thread:
queue.Post(_ => window.Text = "Updated", null);
```

- Work is kept in memory and a single wake-up message is posted to a message-only window of the UI thread. Window messages are
  dispatched by every message loop, **including modal ones** (menus, message boxes, task dialogs, window move/resize), so queued
  work never stalls while one of those is open. Thread messages (`PostThreadMessage`) would be lost there.
- At most 256 items run per wake-up so a flood of posts can't starve input and paint messages.
- `DirectNSynchronizationContext.Send` runs inline on the UI thread and otherwise blocks until the UI thread ran the work.
- Exceptions from queued work raise `UnhandledException`; unless `Handled` is set they are rethrown on the thread pool and
  terminate the process, like WinForms and WPF.
- `TryPost` returns `false` (and `Post` throws) once the queue is closed or its thread has exited; queued work is then dropped
  and a pending `Send` throws `InvalidOperationException` instead of waiting forever.
- Call `Close()` on the owning thread when its message loop has ended for good (the host does this). A queue whose thread
  exits without it is still released by the garbage collector.

| Member | |
|---|---|
| `DirectNMessageQueue.GetOrCreateForCurrentThread()` | The queue of the calling thread, created on first use. |
| `DirectNMessageQueue.Current` | The queue of the calling thread, or `null`. |
| `Post` / `TryPost` | Queue work for the UI thread. |
| `Close()` | Owner thread only: drops queued work, rejects later posts, releases the window and native handle. |
| `CheckAccess()`, `Thread`, `ManagedThreadId` | The owning thread. |
| `SynchronizationContext` | The `DirectNSynchronizationContext` posting to this queue. |
| `UnhandledException` | Exceptions from queued work. |
| `RethrowAsUnhandled(exception)` | Surfaces an exception as unhandled without crossing a native callback boundary. |

Ported from `Win32MessageQueue` / `Win32SynchronizationContext` of Scarlet.AspNetCore.Components.WebView.Win32Aot onto
DirectNAot's interop.
