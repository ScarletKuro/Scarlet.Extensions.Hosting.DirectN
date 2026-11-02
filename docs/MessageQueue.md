# Scarlet.DirectN.Threading

The UI-thread work queue and `SynchronizationContext` for DirectN (Win32) apps, Native AOT compatible. Used by
[Scarlet.Extensions.Hosting.DirectN](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN). There is at most one
queue per UI thread: one ordering, one exception path and one `SynchronizationContext` for everything on it.

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
- Like WPF's Dispatcher and WinForms' `BeginInvoke`, each item runs in the `ExecutionContext` captured from its caller
  (`AsyncLocal` values, culture), and what it changes there doesn't leak into later items. Work posted with flow suppressed
  runs in the UI thread's own baseline context.
- `DirectNSynchronizationContext.Send` runs inline on the UI thread and otherwise blocks until the UI thread ran the work.
- Exceptions from queued work raise `UnhandledException`; unless `Handled` is set they are rethrown on the thread pool and
  terminate the process, like WinForms and WPF.
- `TryPost` returns `false` (and `Post` throws `InvalidOperationException`) only when the work can never run: the queue is
  closed or its thread has exited (`IsClosed`). A full thread message queue is retried, not reported.
- A cross-thread `Send` always returns: the work ran, or it throws `InvalidOperationException` because the queue closed or its
  thread exited first, also when the thread exits without `Close`.
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

See the [wiki](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/9.-Message-queue-%F0%9F%93%AC) for details.
