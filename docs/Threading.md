# Scarlet.Extensions.Hosting.DirectN.Threading

`JoinableTaskFactory`-based thread switching for [Scarlet.Extensions.Hosting.DirectN](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN).

```csharp
builder.Services.AddDirectN();
builder.Services.AddThreadSwitching();

public sealed class WindowService(JoinableTaskFactory jtf)
{
    public async Task ShowAsync()
    {
        await jtf.SwitchToMainThreadAsync();   // now on the DirectN UI thread
        // ... create or update windows ...
        await TaskScheduler.Default;           // back to the thread pool
    }
}
```

`AddThreadSwitching()` registers a `JoinableTaskContext` whose main thread is the DirectN UI thread, and its
`JoinableTaskFactory`. It works before the UI thread has started: `SwitchToMainThreadAsync()` then completes once it has.
Don't wait for that inside a hosted service's `StartAsync` (awaiting the switch, or `JoinableTaskFactory.Run`): the UI
thread only starts after every `StartAsync` has returned, so host startup would hang. Use `BackgroundService.ExecuteAsync`
or `IHostApplicationLifetime.ApplicationStarted` instead.

## Contents

A trimmed, Native AOT compatible extract of
[Microsoft.VisualStudio.Threading](https://github.com/microsoft/vs-threading) `v17.12.19` (MIT, not affiliated with Microsoft).
Type and member names are unchanged, so code moves over by replacing `using Microsoft.VisualStudio.Threading;` with
`using Scarlet.Extensions.Hosting.DirectN.Threading;`.

Public types: `JoinableTaskContext`, `JoinableTaskContextNode`, `JoinableTaskFactory`, `JoinableTask`, `JoinableTask<T>`,
`JoinableTaskCollection`, `JoinableTaskCreationOptions`, `JoinableTaskContextException`, `AsyncLazy<T>`, and
`AwaitExtensions` (`await TaskScheduler.Default`, `SwitchTo`).

```csharp
var settings = new AsyncLazy<Settings>(LoadSettingsAsync, jtf);
var value = await settings.GetValueAsync();
```

## Differences from vs-threading

- Not included: `AsyncReaderWriterLock`, `AsyncSemaphore`, `ReentrantSemaphore`, `AsyncManualResetEvent` (internal), the
  other async primitives, WPF `Dispatcher` support, and the hang *report* (`GetHangReport`; hang *detection*
  `JoinableTaskContext.OnHangDetected` is kept). See [UPSTREAM.md](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/blob/main/UPSTREAM.md) for the full list.
- `AwaitExtensions` has the same name and members as upstream's: in a project that also references
  `Microsoft.VisualStudio.Threading`, `await TaskScheduler.Default` is ambiguous when both namespaces are imported. Import
  only one of them in such files.
- The VSTHRD analyzers (`Microsoft.VisualStudio.Threading.Analyzers`) recognize the `Microsoft.VisualStudio.Threading`
  namespace only, so they don't analyze these types.
- `JoinableTaskFactory.Run` on the UI thread doesn't pump window messages while it waits (as upstream): work that needs the
  UI thread still runs through the factory's own queue, so it doesn't deadlock, but windows don't repaint until it returns.
