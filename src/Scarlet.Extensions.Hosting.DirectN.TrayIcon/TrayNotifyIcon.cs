// Behavior ported from System.Windows.Forms.NotifyIcon (dotnet/winforms, MIT License, Copyright (c) .NET Foundation and Contributors).
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DirectN;
using Scarlet.DirectN.Threading;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;
using static Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal.NativeMethods;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// An icon in the notification area (system tray), with the behavior of WinForms' <c>NotifyIcon</c>: the same mouse events and
/// their order, balloon tips, and re-adding the icon when Explorer restarts. The context menu is a native Win32 popup menu.
/// </summary>
/// <remarks>
/// Create and use it on a UI thread that runs a message loop (the DirectN UI thread); members throw
/// <see cref="InvalidOperationException"/> on other threads, except <see cref="Dispose"/>. Exceptions thrown by event handlers
/// are reported like any other exception on the UI thread (<c>IDirectNThread.UnhandledException</c>).
/// </remarks>
public sealed unsafe class TrayNotifyIcon : IDisposable
{
    private const string WindowClassName = "Scarlet.Extensions.Hosting.DirectN.TrayIcon.NotifyIcon";
    private const uint WM_TRAYMOUSEMESSAGE = WM_USER + 1024;
    private const int MaxTextLength = 127;
    private const int MaxBalloonTextLength = 255;
    private const int MaxBalloonTitleLength = 63;

    private static readonly uint TaskbarCreatedMessage = RegisterMessage("TaskbarCreated");
    private static readonly Lock ClassRegistrationLock = new();
    private static bool _classRegistered;
    private static int s_nextId;

    private readonly int _threadId;
    private readonly uint _id;
    private GCHandle _self;
    private HWND _hwnd;
    private TrayIconImage? _icon;
    private TrayMenu? _contextMenu;
    private string _text = string.Empty;
    private bool _visible;
    private bool _added;
    private bool _doubleClick;
    private bool _rightButtonReleased;
    private bool _menuShowing;

    // Set once by Dispose, from any thread; read on the owner thread, which then stops using the icon.
    private volatile bool _disposed;
    private int _disposeStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayNotifyIcon"/> class. Must be called on the UI thread.
    /// </summary>
    public TrayNotifyIcon()
    {
        _threadId = Environment.CurrentManagedThreadId;
        _id = (uint)Interlocked.Increment(ref s_nextId);
        _self = GCHandle.Alloc(this);
        try
        {
            _hwnd = CreateWindow(GCHandle.ToIntPtr(_self));
        }
        catch
        {
            _self.Free();
            throw;
        }
    }

    /// <summary>Raised when the icon is clicked with any mouse button, or activated with the keyboard.</summary>
    public event EventHandler? Click;

    /// <summary>Raised when the icon is double-clicked with any mouse button.</summary>
    public event EventHandler? DoubleClick;

    /// <summary>Raised when the icon is clicked with the mouse, after <see cref="Click"/>.</summary>
    public event EventHandler<TrayMouseEventArgs>? MouseClick;

    /// <summary>Raised when the icon is double-clicked with the mouse, after <see cref="DoubleClick"/>.</summary>
    public event EventHandler<TrayMouseEventArgs>? MouseDoubleClick;

    /// <summary>Raised when a mouse button is pressed over the icon.</summary>
    public event EventHandler<TrayMouseEventArgs>? MouseDown;

    /// <summary>Raised when a mouse button is released over the icon.</summary>
    public event EventHandler<TrayMouseEventArgs>? MouseUp;

    /// <summary>Raised when the mouse moves over the icon.</summary>
    public event EventHandler<TrayMouseEventArgs>? MouseMove;

    /// <summary>Raised when a balloon tip is shown.</summary>
    public event EventHandler? BalloonTipShown;

    /// <summary>Raised when a balloon tip is clicked.</summary>
    public event EventHandler? BalloonTipClicked;

    /// <summary>Raised when a balloon tip closes (timed out or dismissed).</summary>
    public event EventHandler? BalloonTipClosed;

    /// <summary>
    /// Gets or sets the icon. The icon only appears when <see cref="Visible"/> is <see langword="true"/> and an icon is set.
    /// The <see cref="TrayNotifyIcon"/> doesn't own the image.
    /// </summary>
    public TrayIconImage? Icon
    {
        get => _icon;
        set
        {
            ThrowIfInvalidAccess();
            _icon = value;
            UpdateIcon(_visible);
        }
    }

    /// <summary>
    /// Gets or sets the tooltip text, at most 127 characters.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The text is longer than 127 characters.</exception>
    public string? Text
    {
        get => _text;
        set
        {
            ThrowIfInvalidAccess();
            value ??= string.Empty;
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, MaxTextLength, nameof(value));
            if (value == _text)
            {
                return;
            }

            _text = value;
            if (_added)
            {
                UpdateIcon(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the icon is shown in the notification area.
    /// </summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            ThrowIfInvalidAccess();
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            UpdateIcon(value);
        }
    }

    /// <summary>
    /// Gets or sets the context menu, shown on right-click, Shift+F10 / the context menu key, and on left-click when
    /// <see cref="ShowContextMenuOnLeftClick"/> is set.
    /// </summary>
    public TrayMenu? ContextMenu
    {
        get => _contextMenu;
        set
        {
            ThrowIfInvalidAccess();
            _contextMenu = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether a left-click (or keyboard activation) also opens the <see cref="ContextMenu"/>.
    /// </summary>
    public bool ShowContextMenuOnLeftClick { get; set; }

    /// <summary>
    /// Gets or sets an arbitrary object associated with the icon.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Shows the <see cref="ContextMenu"/> at the mouse position. Returns after the menu has closed and the chosen item's
    /// <see cref="TrayMenuItem.Click"/> handler has run. Does nothing without a menu or while the menu is already open.
    /// </summary>
    public void ShowContextMenu()
    {
        ThrowIfInvalidAccess();
        Functions.GetCursorPos(out var point);
        ShowContextMenu(point.x, point.y);
    }

    /// <summary>
    /// Shows a balloon notification. On Windows 10 and later it appears as a toast and the timeout is decided by the system.
    /// The icon must be visible.
    /// </summary>
    /// <param name="title">The title, at most 63 characters.</param>
    /// <param name="text">The text, at most 255 characters; must not be empty.</param>
    /// <param name="icon">The icon shown in the notification.</param>
    public void ShowBalloonTip(string title, string text, TrayToolTipIcon icon = TrayToolTipIcon.None)
    {
        ThrowIfInvalidAccess();
        ArgumentNullException.ThrowIfNull(title);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(title.Length, MaxBalloonTitleLength, nameof(title));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, MaxBalloonTextLength, nameof(text));

        if (!_added)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = (NOTIFY_ICON_INFOTIP_FLAGS)(uint)icon;
        Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
    }

    /// <summary>
    /// Removes the icon from the notification area and releases its window. Callable from any thread: from another thread
    /// the owning thread does it, the next time it processes messages.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _disposed = true;

        // The icon's state belongs to the owning thread: only that thread removes the icon and destroys the window
        // (in WM_DESTROY, see OnDestroyed), so nothing races with it updating the icon.
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            Functions.DestroyWindow(_hwnd);
            return;
        }

        if (!Functions.PostMessageW(_hwnd, WM_CLOSE, default, default) && !IsWindow(_hwnd.Value))
        {
            // The owning thread has exited (taking the window with it): nothing else can use the icon any more.
            RemoveFromNotificationArea();
            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
    }

    internal HWND WindowHandle => _hwnd;

    internal bool IsAdded => _added;

    internal uint Id => _id;

    // Whether Explorer still has the icon (a modify without changes succeeds only for an existing icon).
    internal bool IsInNotificationArea() => Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_MODIFY, CreateData());

    // Internal so tests can drive the window procedure without Explorer.
    internal void ProcessMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAYMOUSEMESSAGE)
        {
            OnTrayMessage((uint)NativeMethods.LoWord(lParam) & 0xFFFF, NativeMethods.LoWord(wParam), NativeMethods.HiWord(wParam));
        }
        else if (msg == TaskbarCreatedMessage)
        {
            // Explorer (re)started, or the taskbar was recreated (e.g. a DPI change): add the icon again, at the current size.
            _added = false;
            UpdateIcon(_visible);
        }
    }

    private void OnTrayMessage(uint message, int x, int y)
    {
        switch (message)
        {
            case WM_LBUTTONDBLCLK:
                OnMouseDownCore(TrayMouseButtons.Left, 2, x, y);
                break;
            case WM_LBUTTONDOWN:
                OnMouseDownCore(TrayMouseButtons.Left, 1, x, y);
                break;
            case WM_LBUTTONUP:
                var wasDoubleClick = _doubleClick;
                OnMouseUpCore(TrayMouseButtons.Left, x, y);
                if (ShowContextMenuOnLeftClick && !wasDoubleClick)
                {
                    ShowContextMenu(x, y);
                }

                break;
            case WM_MBUTTONDBLCLK:
                OnMouseDownCore(TrayMouseButtons.Middle, 2, x, y);
                break;
            case WM_MBUTTONDOWN:
                OnMouseDownCore(TrayMouseButtons.Middle, 1, x, y);
                break;
            case WM_MBUTTONUP:
                OnMouseUpCore(TrayMouseButtons.Middle, x, y);
                break;
            case WM_RBUTTONDBLCLK:
                OnMouseDownCore(TrayMouseButtons.Right, 2, x, y);
                break;
            case WM_RBUTTONDOWN:
                OnMouseDownCore(TrayMouseButtons.Right, 1, x, y);
                break;
            case WM_RBUTTONUP:
                OnMouseUpCore(TrayMouseButtons.Right, x, y);
                _rightButtonReleased = true;
                break;
            case WM_MOUSEMOVE:
                MouseMove?.Invoke(this, new TrayMouseEventArgs(TrayMouseButtons.None, 0, x, y));
                break;
            case WM_CONTEXTMENU:
                // Sent after a right-click and for Shift+F10 / the context menu key.
                var fromKeyboard = !_rightButtonReleased;
                _rightButtonReleased = false;
                ShowContextMenu(x, y, fromKeyboard);
                break;
            case NIN_KEYSELECT:
                // Enter or Space on the focused icon.
                Click?.Invoke(this, new TrayMouseEventArgs(TrayMouseButtons.None, 0, x, y));
                if (ShowContextMenuOnLeftClick)
                {
                    ShowContextMenu(x, y, fromKeyboard: true);
                }

                break;
            case NIN_BALLOONSHOW:
                BalloonTipShown?.Invoke(this, EventArgs.Empty);
                break;
            case NIN_BALLOONHIDE:
            case NIN_BALLOONTIMEOUT:
                BalloonTipClosed?.Invoke(this, EventArgs.Empty);
                break;
            case NIN_BALLOONUSERCLICK:
                BalloonTipClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnMouseDownCore(TrayMouseButtons button, int clicks, int x, int y)
    {
        var args = new TrayMouseEventArgs(button, clicks, x, y);
        if (clicks == 2)
        {
            // WinForms order: DoubleClick, then MouseDoubleClick.
            DoubleClick?.Invoke(this, args);
            MouseDoubleClick?.Invoke(this, args);
            _doubleClick = true;
        }

        MouseDown?.Invoke(this, args);
    }

    private void OnMouseUpCore(TrayMouseButtons button, int x, int y)
    {
        // As in WinForms, release events carry no click count.
        var args = new TrayMouseEventArgs(button, 0, x, y);
        MouseUp?.Invoke(this, args);

        // A double-click raises DoubleClick instead of a second Click.
        if (!_doubleClick)
        {
            Click?.Invoke(this, args);
            MouseClick?.Invoke(this, args);
        }

        _doubleClick = false;
    }

    private void ShowContextMenu(int x, int y, bool fromKeyboard = false)
    {
        if (_contextMenu is not { } menu || _menuShowing || _disposed)
        {
            return;
        }

        _menuShowing = true;
        TrayMenuItem? chosen;
        try
        {
            menu.OnOpening();
            using var nativeMenu = NativeMenu.Create(menu);

            // Without this the menu doesn't close when the user clicks elsewhere (KB135788).
            Functions.SetForegroundWindow(_hwnd);
            var align = Functions.GetSystemMetrics((SYSTEM_METRICS_INDEX)SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            var command = Functions.TrackPopupMenuEx(
                nativeMenu.Handle,
                TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align,
                x,
                y,
                _hwnd,
                0);
            Functions.PostMessageW(_hwnd, WM_NULL, default, default);

            chosen = command.Value > 0 ? nativeMenu.FindCommand((uint)command.Value) : null;
        }
        finally
        {
            _menuShowing = false;
        }

        // Opened from the keyboard: hand the focus back to the notification area, as the Shell asks.
        if (fromKeyboard && _added && !_disposed)
        {
            Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_SETFOCUS, CreateData());
        }

        // After the menu has closed, so a handler can show a dialog or the menu again; not once disposed meanwhile.
        if (!_disposed && chosen is { Enabled: true, IsSeparator: false } && chosen.Items.Count == 0)
        {
            chosen.PerformClick();
        }
    }

    private void OnDestroyed()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        _disposed = true;
        RemoveFromNotificationArea();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void RemoveFromNotificationArea()
    {
        if (_added)
        {
            Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_DELETE, CreateData());
            _added = false;
        }
    }

    private void UpdateIcon(bool show)
    {
        if (_disposed || _hwnd.Value == 0)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = WM_TRAYMOUSEMESSAGE;
        data.szTip = _text;

        nint iconHandle = 0;
        if (_icon is { } icon)
        {
            iconHandle = icon.GetHandle(GetIconSize());
            data.uFlags |= NOTIFY_ICON_DATA_FLAGS.NIF_ICON;
            data.hIcon = new HICON { Value = iconHandle };
        }

        if (show && iconHandle != 0)
        {
            if (!_added)
            {
                // NIM_ADD fails while Explorer isn't running (TaskbarCreated adds it once it is), and when the icon still exists:
                // TaskbarCreated is also sent when the taskbar is recreated without losing icons (e.g. a DPI change). Then the
                // icon is updated instead; otherwise it would be orphaned, never removed by hiding or disposing.
                if (Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_ADD, data)
                    || Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data))
                {
                    data.Anonymous.uVersion = Constants.NOTIFYICON_VERSION_4;
                    Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
                    _added = true;
                }
            }
            else
            {
                Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
            }
        }
        else if (_added)
        {
            Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
            _added = false;
        }
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = _id,
    };

    private int GetIconSize()
    {
        // The notification area uses small icons at the DPI of the primary monitor (the hidden window's DPI).
        var size = 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) && Functions.GetDpiForWindow(_hwnd) is var dpi and not 0)
        {
            size = Functions.GetSystemMetricsForDpi((SYSTEM_METRICS_INDEX)SM_CXSMICON, dpi);
        }

        if (size <= 0)
        {
            size = Functions.GetSystemMetrics((SYSTEM_METRICS_INDEX)SM_CXSMICON);
        }

        return size > 0 ? size : 16;
    }

    private void ThrowIfInvalidAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("A TrayNotifyIcon can only be used on the thread that created it.");
        }
    }

    private static uint RegisterMessage(string name)
    {
        fixed (char* p = name)
        {
            return Functions.RegisterWindowMessageW(new PWSTR { Value = (nint)p });
        }
    }

    private static HWND CreateWindow(nint self)
    {
        EnsureWindowClassRegistered();

        HWND hwnd;
        fixed (char* className = WindowClassName)
        {
            // A hidden top-level window: message-only windows don't receive the broadcast TaskbarCreated message.
            hwnd = Functions.CreateWindowExW(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                new PWSTR { Value = (nint)className },
                default,
                0,
                0, 0, 0, 0,
                default,
                default,
                new HINSTANCE(Functions.GetModuleHandleW(default).Value),
                0);
        }

        if (hwnd.Value == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        Functions.SetWindowLongPtrW(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, self);

        // Lets Explorer's TaskbarCreated through when this process runs elevated.
        ChangeWindowMessageFilterEx(hwnd.Value, TaskbarCreatedMessage, MSGFLT_ALLOW, 0);
        return hwnd;
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (ClassRegistrationLock)
        {
            if (_classRegistered)
            {
                return;
            }

            fixed (char* className = WindowClassName)
            {
                var wc = new WNDCLASSW
                {
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                    hInstance = new HINSTANCE(Functions.GetModuleHandleW(default).Value),
                    lpszClassName = new PWSTR { Value = (nint)className },
                };

                if (Functions.RegisterClassW(wc) == 0)
                {
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ERROR_CLASS_ALREADY_EXISTS)
                    {
                        throw new Win32Exception(error);
                    }
                }
            }

            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var userData = Functions.GetWindowLongPtrW(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        if (userData != 0 && GCHandle.FromIntPtr(userData) is { IsAllocated: true, Target: TrayNotifyIcon icon })
        {
            if (msg == WM_DESTROY)
            {
                // Dispose on the owning thread, a cross-thread Dispose (WM_CLOSE), or someone else closing the window.
                Functions.SetWindowLongPtrW(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                icon.OnDestroyed();
            }
            else
            {
                try
                {
                    icon.ProcessMessage(msg, wParam, lParam);
                }
#pragma warning disable CA1031 // Exceptions must not cross the native boundary; reported like any other UI-thread exception.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    ReportException(exception);
                }
            }
        }

        return Functions.DefWindowProcW(new HWND(hwnd), msg, new WPARAM { Value = (nuint)wParam }, new LPARAM { Value = lParam }).Value;
    }

    private static void ReportException(Exception exception)
    {
        // Through the UI thread's queue, so IDirectNThread.UnhandledException sees it (and can handle it).
        var edi = ExceptionDispatchInfo.Capture(exception);
        if (DirectNMessageQueue.Current is not { } queue || !queue.TryPost(static state => ((ExceptionDispatchInfo)state!).Throw(), edi))
        {
            DirectNMessageQueue.RethrowAsUnhandled(exception);
        }
    }
}
