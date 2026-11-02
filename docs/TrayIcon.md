# Scarlet.Extensions.Hosting.DirectN.TrayIcon

A notification area (tray) icon for [Scarlet.Extensions.Hosting.DirectN](../README.md), Native AOT compatible.

`TrayNotifyIcon` ports the behavior of WinForms' `NotifyIcon`: the same mouse events in the same order, balloon tips, and
re-adding the icon when Explorer restarts. The context menu is a native Win32 popup menu (Windows 11 style, dark mode,
mnemonics and keyboard access), rebuilt from a `TrayMenu` each time it opens. Types are prefixed `Tray` so they don't clash
with `DirectN.Extensions.Utilities.NotifyIcon`.

```csharp
builder.Services.AddDirectN(o => o.ShutdownMode = ShutdownMode.OnExplicitShutdown);
builder.Services.AddDirectNTrayIcon<TrayIcon>();

public sealed class TrayIcon(IDirectNThread thread) : ITrayIcon
{
    private TrayNotifyIcon? _notifyIcon;
    private TrayIconImage? _image;

    public void InitializeComponent()   // on the UI thread, before the message loop
    {
        _image = TrayIconImage.FromApplicationIcon();   // <ApplicationIcon> of the exe
        _notifyIcon = new TrayNotifyIcon
        {
            Icon = _image,
            Text = $"My app {ApplicationInfo.ProductVersion}",
            ShowContextMenuOnLeftClick = true,
            ContextMenu = new TrayMenu
            {
                Items =
                {
                    new TrayMenuItem(ApplicationInfo.ProductVersion) { Enabled = false },
                    TrayMenuItem.Separator(),
                    new TrayMenuItem("E&xit", (_, _) => thread.HandleApplicationExit()),
                },
            },
            Visible = true,
        };
    }

    public void Dispose()               // on the UI thread, after the message loop
    {
        _notifyIcon?.Dispose();
        _image?.Dispose();
    }
}
```

## API

| Type | |
|---|---|
| `ITrayIcon` | An `IDirectNComponent` + `IDisposable`; register with `AddDirectNTrayIcon<T>()` (or a factory). |
| `TrayNotifyIcon` | `Icon`, `Text` (max 127 chars), `Visible`, `ContextMenu`, `ShowContextMenuOnLeftClick`, `ShowContextMenu()`, `ShowBalloonTip(title, text, icon)`; events `Click`, `DoubleClick`, `MouseClick`, `MouseDoubleClick`, `MouseDown`, `MouseUp`, `MouseMove`, `BalloonTipShown`, `BalloonTipClicked`, `BalloonTipClosed`. UI thread only (except `Dispose`). |
| `TrayMenu` | `Items`, `Opening` event (update items right before the menu shows). |
| `TrayMenuItem` | `Text` (`&` mnemonics), `Enabled`, `Checked`, `IsDefault` (bold), `Items` (submenu), `Tag`, `Click`; `TrayMenuItem.Separator()`. |
| `TrayIconImage` | `FromApplicationIcon()`, `FromResource(id)`, `FromFile(path)`, `FromStream(ico)`, `FromHandle(hicon, ownsHandle)`. Loaded at the notification area's size for the current DPI. Not owned by the icon: dispose it after the icon. |
| `ApplicationInfo` | `ProductVersion`, `ProductName`: AOT-safe replacements for WinForms' `Application.ProductVersion` / `ProductName`. |

## Differences from WinForms

- `ShowContextMenu()` is public: no reflection needed to open the menu on left click (and `ShowContextMenuOnLeftClick` does it for you).
- Uses `NOTIFYICON_VERSION_4`: the menu also opens with Shift+F10 / the context menu key, Enter/Space on the focused icon raise `Click`, and positions are correct on every monitor.
- A chosen menu item's `Click` runs after the menu has closed.
- Exceptions thrown by event handlers go to `IDirectNThread.UnhandledException` like any other UI-thread exception.
