# Scarlet.Extensions.Hosting.DirectN.TrayIcon

A notification area (tray) icon for [Scarlet.Extensions.Hosting.DirectN](https://github.com/ScarletKuro/Scarlet.Extensions.Hosting.DirectN), Native AOT compatible.

`TrayNotifyIcon` ports the behavior of WinForms' `NotifyIcon`: the same mouse events in the same order, balloon tips, and
re-adding the icon when Explorer restarts. The context menu is a native Win32 popup menu (system style, mnemonics and
keyboard access), rebuilt from a `TrayMenu` each time it opens. Types are prefixed `Tray` so they don't clash
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
- With a right-click, the menu opens on `WM_CONTEXTMENU`, after `MouseUp`, `Click` and `MouseClick` (WinForms opens it on the button release, before them).
- `Dispose` can be called from any thread: the owning thread removes the icon the next time it processes messages.
- Exceptions thrown by event handlers go to `IDirectNThread.UnhandledException` like any other UI-thread exception.
