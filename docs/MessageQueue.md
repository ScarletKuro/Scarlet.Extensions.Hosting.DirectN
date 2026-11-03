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
queue.Post(() => window.Text = "Updated");
bool accepted = queue.TryPost(() => window.Text = "Updated again");

// Await completion, observe failures, or return a value:
await queue.InvokeAsync(() => { window.Text = "Updated"; }, cancellationToken);
var title = await queue.InvokeAsync(() => window.Text, cancellationToken);
```

`DirectNMessageQueue` inherits `DirectNDispatcher`, which provides fire-and-forget posting and four awaitable overloads:

```csharp
void Post(Action callback);
bool TryPost(Action callback);
Task InvokeAsync(Action callback, CancellationToken cancellationToken = default);
Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken = default);
Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default);
Task<T> InvokeAsync<T>(Func<Task<T>> callback, CancellationToken cancellationToken = default);
```

`Post` and `TryPost` always enqueue, including on the UI thread. `Post` throws `InvalidOperationException` if the work is
rejected; `TryPost` returns `false`. Accepted posts may still be dropped during shutdown. Exceptions thrown by posted
callbacks use the queue's `UnhandledException` event. Use `InvokeAsync` for asynchronous callbacks or to observe completion
and exceptions. The queue also retains its `SendOrPostCallback` and state overloads for lower-level integrations.

Invocations always enqueue, including on the UI thread. The async overloads wait for the full callback, with UI context
capture working across its awaits unless it uses `ConfigureAwait(false)`. Callback exceptions fault the returned task
instead of raising `UnhandledException`; callback cancellation produces a canceled task. Task continuations run
asynchronously. Cancellation prevents a queued callback from starting; once it starts, it controls its own cancellation.
Queued callbacks still occupy their queue entries until drained, even after cancellation.

Rejection, closing the queue, or owner-thread exit faults invocations that have not started with `InvalidOperationException`.
A running async callback is responsible for completing: keep the UI loop alive while it needs UI continuations. Neither
shutdown nor the invocation's cancellation token interrupts a running callback. Never use `.Wait()` or `.Result` on the
UI thread to wait for an invocation. `CheckAccess()` tests thread ownership; `VerifyAccess()` throws outside the UI thread.

Under the Generic Host, inject `DirectNDispatcher` or use `IDirectNThread.Dispatcher`. It buffers calls before UI startup,
faults buffered calls on failed startup or disposal, and uses the queue once available. Do not await an invocation in a
hosted service's `StartAsync`: the UI starts after every `StartAsync` returns. Use a background service or
`IHostApplicationLifetime.ApplicationStarted`. Work buffered before the queue exists waits until application startup;
work posted once the queue exists may run earlier, including during startup modal loops.

Custom `DirectNDispatcher` subclasses must implement the documented `TryEnqueue` contract: enqueue safely from any thread,
preserve execution context, install the UI synchronization context, and notify accepted work of execution or abandonment.
Shutdown rejection returns `false`; it must not throw from the enqueue implementation.

- Work is kept in memory and a single wake-up message is posted to a message-only window of the UI thread. Window messages are
  dispatched by every message loop, **including modal ones** (menus, message boxes, task dialogs, window move/resize), so queued
  work never stalls while one of those is open. Thread messages (`PostThreadMessage`) would be lost there.
- At most 256 items, and no new item after about 15 ms (a frame), run per wake-up; a single long item can't be interrupted.
  When more are queued and input, paint or timer messages are pending, the queue continues from a timer (retrieved after
  all of them, as WPF does for background work), so a flood of posts can't starve them.
- Like WPF's Dispatcher and WinForms' `BeginInvoke`, each item runs in the `ExecutionContext` captured from its caller
  (`AsyncLocal` values, culture), and what it changes there doesn't leak into later items. Work posted with flow suppressed
  runs in the UI thread's own baseline context.
- `DirectNSynchronizationContext.Send` runs inline on the UI thread and otherwise blocks until the UI thread ran the work.
- Exceptions from `Post` callbacks raise `UnhandledException`; unless `Handled` is set they are rethrown on the thread pool and
  terminate the process, like WinForms and WPF.
- `TryPost` returns `false` (and `Post` throws `InvalidOperationException`) only when the work can never run: the queue is
  closed or its thread has exited (`IsClosed`). A full thread message queue is retried, not reported.
- `DirectNSynchronizationContext.Post` never throws: once the queue is closed the work is dropped, like WPF's and WinForms'
  contexts after shutdown. It is the context `await` resumes on, and the runtime turns an exception thrown there into one that
  terminates the process. `DroppedCount` counts the work dropped by closing.
- A cross-thread `Send` returns once the work ran, or throws `InvalidOperationException` because the queue closed or its
  thread exited first, also when the thread exits without `Close`. Like any `Send`, it waits as long as the UI thread is alive
  but doesn't process messages (blocked, or busy in a long callback).
- The queue has no capacity limit: don't flood the UI thread with unbounded work.
- Call `Close()` on the owning thread when its message loop has ended for good (the host does this). A queue whose thread
  exits without it is still released by the garbage collector.
- Queue creation requires a native owner-thread handle to observe unexpected exit. If acquiring it fails, creation throws
  `Win32Exception`, rather than accepting work whose completion could no longer be guaranteed before execution.

| Member | |
|---|---|
| `DirectNMessageQueue.GetOrCreateForCurrentThread()` | The queue of the calling thread, created on first use. |
| `DirectNMessageQueue.Current` | The queue of the calling thread, or `null`. |
| `Post` / `TryPost` | Queue work for the UI thread. |
| `InvokeAsync` | Await an action, result, or asynchronous callback on the UI thread. |
| `Close()` | Owner thread only: drops queued work, rejects later posts, releases the window and native handle. |
| `CheckAccess()`, `VerifyAccess()`, `Thread`, `ManagedThreadId` | Test or enforce access to the owning thread. |
| `SynchronizationContext` | The `DirectNSynchronizationContext` posting to this queue. |
| `UnhandledException` | Exceptions from queued work. |
| `RethrowAsUnhandled(exception)` | Surfaces an exception as unhandled without crossing a native callback boundary. |

See the [wiki](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/wiki/9.-Message-queue-%F0%9F%93%AC) for details.
