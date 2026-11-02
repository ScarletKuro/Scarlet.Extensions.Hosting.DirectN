# Scarlet.Extensions.Hosting.DirectN.Threading

`JoinableTaskFactory`-based thread switching for [Scarlet.Extensions.Hosting.DirectN](../README.md).

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

## Contents

A trimmed, Native AOT compatible extract of
[Microsoft.VisualStudio.Threading](https://github.com/microsoft/vs-threading) `v17.12.19` (MIT, not affiliated with Microsoft).
Type and member names are unchanged, so code moves over by replacing `using Microsoft.VisualStudio.Threading;` with
`using Scarlet.Extensions.Hosting.DirectN.Threading;`.

Public types: `JoinableTaskContext`, `JoinableTaskContextNode`, `JoinableTaskFactory`, `JoinableTask`, `JoinableTask<T>`,
`JoinableTaskCollection`, `JoinableTaskCreationOptions`, `JoinableTaskContextException`, `AsyncLazy<T>`, and
`AwaitExtensions` (`await TaskScheduler.Default`, `SwitchTo`).

```csharp
var environment = new AsyncLazy<CoreWebView2Environment>(CreateEnvironmentAsync, jtf);
var value = await environment.GetValueAsync();
```

## Differences from vs-threading

- Not included: `AsyncReaderWriterLock`, `AsyncSemaphore`, `ReentrantSemaphore`, `AsyncManualResetEvent` (internal), the
  other async primitives, WPF `Dispatcher` support, and the hang *report* (`GetHangReport`; hang *detection*
  `JoinableTaskContext.OnHangDetected` is kept). See [UPSTREAM.md](../UPSTREAM.md) for the full list.
- The VSTHRD analyzers (`Microsoft.VisualStudio.Threading.Analyzers`) recognize the `Microsoft.VisualStudio.Threading`
  namespace only, so they don't analyze these types.
- `JoinableTaskFactory.Run` on the UI thread doesn't pump window messages while it waits (as upstream): work that needs the
  UI thread still runs through the factory's own queue, so it doesn't deadlock, but windows don't repaint until it returns.
