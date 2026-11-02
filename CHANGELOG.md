# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-11-02

First release: Microsoft.Extensions.Hosting for DirectN (Win32 / Direct2D) applications, Native AOT compatible (`net10.0-windows`).

### Added
- `Scarlet.Extensions.Hosting.DirectN`: `AddDirectN` (`ShutdownMode`, `OnStartup`, `OnExit`, `ShowFatalErrorDialog`), `IDirectNComponent` / `AddDirectNComponent`, `DirectNLifetime` / `AddDirectNLifetime` / `UseDirectNLifetime`, `IDirectNThread` / `IDirectNContext`. The lifecycle follows Extensions.Hosting.Wpf 5: the UI thread starts after every hosted service has started and stops before them, startup failures fail host startup, and cleanup runs on the UI thread while every service still works.
- `Scarlet.Extensions.Hosting.DirectN.Threading`: `AddThreadSwitching`, with a trimmed, Native AOT compatible extract of Microsoft.VisualStudio.Threading 17.12.19 (`JoinableTaskFactory`, `AsyncLazy<T>`, `await TaskScheduler.Default`).
- `Scarlet.Extensions.Hosting.DirectN.TrayIcon`: `TrayNotifyIcon` (WinForms `NotifyIcon` behavior, native Win32 menu), `TrayMenu` / `TrayMenuItem`, `TrayIconImage`, `ApplicationInfo`, `AddDirectNTrayIcon`.
- `Scarlet.DirectN.Threading`: `DirectNMessageQueue` / `DirectNSynchronizationContext`, the UI-thread queue under the host. Work is delivered inside modal loops, runs in the caller's `ExecutionContext`, and `Send` always returns.

[Unreleased]: https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/compare/HEAD..1.0.0
[1.0.0]: https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN/releases/tag/1.0.0
