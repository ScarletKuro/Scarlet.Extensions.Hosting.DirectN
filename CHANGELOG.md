# Changelog

## Unreleased

First version: Microsoft.Extensions.Hosting for DirectN (Win32 / Direct2D) apps, Native AOT compatible (`net10.0-windows`).

- `Scarlet.Extensions.Hosting.DirectN`: `AddDirectN` (ShutdownMode, OnStartup, OnExit, fatal-error dialog option),
  `IDirectNComponent` / `AddDirectNComponent`, `DirectNLifetime`, `IDirectNThread` / `IDirectNContext`. Lifecycle ported from
  Extensions.Hosting.Wpf 5.0.0 (start after hosted services, startup failures fail the host, cleanup while services still
  work, pre-start posts queued).
- `Scarlet.Extensions.Hosting.DirectN.Threading`: `AddThreadSwitching`, plus a trimmed AOT-compatible extract of
  Microsoft.VisualStudio.Threading v17.12.19 (`JoinableTaskFactory`, `AsyncLazy<T>`, `await TaskScheduler.Default`).
- `Scarlet.Extensions.Hosting.DirectN.TrayIcon`: `TrayNotifyIcon` (WinForms `NotifyIcon` behavior, native Win32 menu),
  `TrayMenu` / `TrayMenuItem`, `TrayIconImage`, `ApplicationInfo`, `AddDirectNTrayIcon`.
- `Scarlet.DirectN.Threading`: `DirectNMessageQueue` / `DirectNSynchronizationContext`, the per-thread UI queue to share with
  Scarlet.AspNetCore.Components.WebView.Win32Aot.
