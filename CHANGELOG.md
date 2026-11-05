# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-11-05

### Added
- `DirectNDispatcher.Post(Action)` and `TryPost(Action)` for fire-and-forget UI work, available through the queue and host dispatcher. Document the subclass enqueue contract, including execution context, rejection and abandonment guarantees.
- `DirectNDispatcher` with `InvokeAsync` overloads for actions, results and asynchronous callbacks, cancellation before execution, and `VerifyAccess`. `DirectNMessageQueue` inherits this API; `IDirectNThread.Dispatcher` and a registered `DirectNDispatcher` expose it through the host, including startup buffering and failure notification.
- `DirectNMessageQueue.TryPost(callback, state, onAbandoned)`: queue work with a callback for when it is dropped without running (the queue closes or its owner thread exits first). Exactly one of the two runs for accepted work.

### Changed
- **Breaking for custom implementations:** `IDirectNThread` gains a `Dispatcher` property. Code consuming the interface is unaffected; classes implementing it must add the member.

### Fixed
- Release owner-thread resources even when window loss already closed the queue. Abandon pending work immediately on native window destruction, roll back failed queue construction, and guard the entire native callback boundary.
- Require a native owner-thread handle when creating a queue, failing explicitly if it cannot be opened. Every successful queue creation now establishes owner-exit monitoring; `Send` no longer needs fallback polling.
- Observe owner-thread exit without requiring another post, so accepted invocations are faulted if their queue is abandoned without `Close()`.

## [1.0.0] - 2026-11-02

First release: Microsoft.Extensions.Hosting for DirectN (Win32 / Direct2D) applications, Native AOT compatible (`net10.0-windows`).

### Added
- `Scarlet.Extensions.Hosting.DirectN`: `AddDirectN` (`ShutdownMode`, `OnStartup`, `OnExit`, `ShowFatalErrorDialog`), `IDirectNComponent` / `AddDirectNComponent`, `DirectNLifetime` / `AddDirectNLifetime` / `UseDirectNLifetime`, `IDirectNThread` / `IDirectNContext`. The lifecycle follows Extensions.Hosting.Wpf 5: the UI thread starts after every hosted service has started and stops before them, startup failures fail host startup, and cleanup runs on the UI thread while every service still works. Confirmed Windows session ends and Restart Manager shutdown requests are handled, giving application cleanup and hosted services a bounded opportunity to finish before process termination.
- `Scarlet.Extensions.Hosting.DirectN.Threading`: `AddThreadSwitching`, with a trimmed, Native AOT compatible extract of Microsoft.VisualStudio.Threading 17.12.19 (`JoinableTaskFactory`, `AsyncLazy<T>`, `await TaskScheduler.Default`).
- `Scarlet.Extensions.Hosting.DirectN.TrayIcon`: `TrayNotifyIcon` (WinForms `NotifyIcon` behavior, native Win32 menu), `TrayMenu` / `TrayMenuItem`, `TrayIconImage`, `ApplicationInfo`, `AddDirectNTrayIcon`.
- `Scarlet.DirectN.Threading`: `DirectNMessageQueue` / `DirectNSynchronizationContext`, the UI-thread queue under the host. Work is delivered inside modal loops, runs in the caller's `ExecutionContext`, can't starve input, paint and timers, posting through the context never throws, and `Send` always returns.

[Unreleased]: https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/compare/1.1.0...HEAD
[1.1.0]: https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/compare/1.0.0...1.1.0
[1.0.0]: https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/releases/tag/1.0.0
