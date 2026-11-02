using DirectN;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;
using static Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal.NativeMethods;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Tests;

/// <summary>
/// Feeds the tray callback messages Explorer would send (NOTIFYICON_VERSION_4 layout) and checks the WinForms event sequence.
/// The icon is never made visible, so nothing is added to the real notification area.
/// </summary>
public class TrayNotifyIconTests
{
    private const uint TrayMessage = WM_USER + 1024;

    [Fact]
    public void ClickRaisesMouseDownMouseUpClickMouseClick()
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, WM_LBUTTONDOWN, 10, 20);
        Send(icon, WM_LBUTTONUP, 10, 20);

        Assert.Equal(["MouseDown:Left:1:10:20", "MouseUp:Left:0:10:20", "Click", "MouseClick:Left:0:10:20"], events);
    }

    [Fact]
    public void DoubleClickRaisesDoubleClickInsteadOfASecondClick()
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, WM_LBUTTONDOWN);
        Send(icon, WM_LBUTTONUP);
        Send(icon, WM_LBUTTONDBLCLK);
        Send(icon, WM_LBUTTONUP);

        Assert.Equal(
            [
                "MouseDown:Left:1:0:0", "MouseUp:Left:0:0:0", "Click", "MouseClick:Left:0:0:0",
                "DoubleClick", "MouseDoubleClick:Left:2:0:0", "MouseDown:Left:2:0:0", "MouseUp:Left:0:0:0",
            ],
            events);
    }

    [Theory]
    [InlineData(WM_RBUTTONDOWN, WM_RBUTTONUP, "Right")]
    [InlineData(WM_MBUTTONDOWN, WM_MBUTTONUP, "Middle")]
    public void OtherButtonsClickLikeWinForms(uint down, uint up, string button)
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, down);
        Send(icon, up);

        Assert.Equal([$"MouseDown:{button}:1:0:0", $"MouseUp:{button}:0:0:0", "Click", $"MouseClick:{button}:0:0:0"], events);
    }

    [Fact]
    public void NegativeCoordinatesOnSecondaryMonitorsAreKept()
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, WM_MOUSEMOVE, -1200, -5);

        Assert.Equal(["MouseMove:None:0:-1200:-5"], events);
    }

    [Fact]
    public void KeyboardActivationRaisesClick()
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, NIN_KEYSELECT);

        Assert.Equal(["Click"], events);
    }

    [Fact]
    public void BalloonNotifications()
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, NIN_BALLOONSHOW);
        Send(icon, NIN_BALLOONUSERCLICK);
        Send(icon, NIN_BALLOONTIMEOUT);
        Send(icon, NIN_BALLOONHIDE);

        Assert.Equal(["BalloonTipShown", "BalloonTipClicked", "BalloonTipClosed", "BalloonTipClosed"], events);
    }

    [Fact]
    public void TextIsLimitedTo127Characters()
    {
        using var icon = new TrayNotifyIcon();

        icon.Text = new string('a', 127);
        Assert.Throws<ArgumentOutOfRangeException>(() => icon.Text = new string('a', 128));
        icon.Text = null;
        Assert.Equal(string.Empty, icon.Text);
    }

    [Fact]
    public void ShowBalloonTipValidatesItsArguments()
    {
        using var icon = new TrayNotifyIcon();

        Assert.Throws<ArgumentException>(() => icon.ShowBalloonTip("title", string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => icon.ShowBalloonTip(new string('t', 64), "text"));
        Assert.Throws<ArgumentOutOfRangeException>(() => icon.ShowBalloonTip("title", new string('t', 256)));

        // Not visible: nothing to show, no error.
        icon.ShowBalloonTip("title", "text", TrayToolTipIcon.Info);
    }

    [Fact]
    public void OnlyTheCreatingThreadMayUseIt()
    {
        using var icon = new TrayNotifyIcon();
        var failures = new List<Exception?>();

        // A dedicated thread: with Task.Run the pool could pick this very thread (xunit.v3.aot runs tests on pool threads).
        var other = new Thread(() =>
        {
            failures.Add(Xunit.Record.Exception(() => icon.Text = "other thread"));
            failures.Add(Xunit.Record.Exception(() => icon.Visible = true));
            failures.Add(Xunit.Record.Exception(icon.ShowContextMenu));
        });
        other.Start();
        other.Join();

        Assert.All(failures, failure => Assert.IsType<InvalidOperationException>(failure));
    }

    [Fact]
    public void DisposeDestroysTheWindowAndIsIdempotent()
    {
        var icon = new TrayNotifyIcon();
        var hwnd = icon.WindowHandle;
        Assert.True(Functions.IsWindow(hwnd));

        icon.Dispose();
        icon.Dispose();

        Assert.False(Functions.IsWindow(hwnd));
        Assert.Throws<ObjectDisposedException>(() => icon.Text = "disposed");
    }

    [Fact]
    public void ShowContextMenuWithoutMenuDoesNothing()
    {
        using var icon = new TrayNotifyIcon();

        icon.ShowContextMenu();
    }

    [Fact]
    public void LeftClickContextMenuAndKeyboardRequestsWithoutAMenuDoNothing()
    {
        using var icon = new TrayNotifyIcon { ShowContextMenuOnLeftClick = true };
        var events = Record(icon);

        Send(icon, WM_LBUTTONDOWN);
        Send(icon, WM_LBUTTONUP);
        Send(icon, WM_CONTEXTMENU);
        Send(icon, NIN_KEYSELECT);

        Assert.Equal(["MouseDown:Left:1:0:0", "MouseUp:Left:0:0:0", "Click", "MouseClick:Left:0:0:0", "Click"], events);
        Assert.True(icon.ShowContextMenuOnLeftClick);
    }

    [Theory]
    [InlineData(WM_MBUTTONDBLCLK, "Middle")]
    [InlineData(WM_RBUTTONDBLCLK, "Right")]
    public void OtherButtonsDoubleClick(uint message, string button)
    {
        using var icon = new TrayNotifyIcon();
        var events = Record(icon);

        Send(icon, message);

        Assert.Equal(["DoubleClick", $"MouseDoubleClick:{button}:2:0:0", $"MouseDown:{button}:2:0:0"], events);
    }

    [Fact]
    public void PropertiesRoundTrip()
    {
        using var icon = new TrayNotifyIcon();
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        var menu = new TrayMenu();

        icon.Icon = image;
        icon.ContextMenu = menu;
        icon.Tag = "tag";
        icon.Text = "same";
        icon.Text = "same";

        Assert.Same(image, icon.Icon);
        Assert.Same(menu, icon.ContextMenu);
        Assert.Equal("tag", icon.Tag);
        Assert.Equal("same", icon.Text);
        Assert.False(icon.Visible);
    }

    [Fact]
    public void VisibleIconIsAddedUpdatedAndRemoved()
    {
        // Adds a real notification area icon briefly (when Explorer runs), then removes it.
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        using var other = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(32)));
        using var icon = new TrayNotifyIcon { Icon = image, Text = "Scarlet test icon" };

        icon.Visible = true;
        Assert.SkipUnless(icon.IsAdded, "No notification area (Explorer isn't running).");
        Assert.True(icon.IsInNotificationArea());

        icon.Visible = true;
        icon.Text = "Scarlet test icon, renamed";
        icon.Icon = other;
        icon.ShowBalloonTip("Scarlet test", "Balloon", TrayToolTipIcon.Warning);

        // The taskbar was recreated without losing the icon (e.g. a DPI change): it must stay tracked, not be orphaned.
        icon.ProcessMessage(TaskbarCreatedMessage, 0, 0);
        Assert.True(icon.IsAdded);

        icon.Visible = false;
        icon.Visible = false;
        Assert.False(icon.Visible);
        Assert.False(icon.IsInNotificationArea());
    }

    [Fact]
    public void DisposeRemovesAVisibleIcon()
    {
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        var icon = new TrayNotifyIcon { Icon = image, Visible = true };
        Assert.SkipUnless(icon.IsAdded, "No notification area (Explorer isn't running).");

        icon.Dispose();

        Assert.False(icon.IsAdded);
    }

    [Fact]
    public void TaskbarCreatedWhileHiddenDoesNothing()
    {
        using var icon = new TrayNotifyIcon();

        icon.ProcessMessage(TaskbarCreatedMessage, 0, 0);

        Assert.False(icon.Visible);
    }

    [Fact]
    public async Task ContextMenuOpensRaisesOpeningAndRunsTheChosenItemAfterClosing()
    {
        using var icon = new TrayNotifyIcon();
        var events = new List<string>();
        var menu = new TrayMenu
        {
            Items =
            {
                new TrayMenuItem("Disabled") { Enabled = false },
                new TrayMenuItem("&Choose", (_, _) => events.Add("Clicked")),
            },
        };
        menu.Opening += (_, _) => events.Add("Opening");
        icon.ContextMenu = menu;

        // The menu runs a modal loop on this thread: choose an item from another thread with its mnemonic.
        var chooser = Task.Run(ChooseMenuItemByMnemonic, TestContext.Current.CancellationToken);
        icon.ShowContextMenu();
        var eventsAfterMenu = events.ToList();

        await chooser.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["Opening", "Clicked"], eventsAfterMenu);
    }

    [Fact]
    public void DisposeFromAnotherThreadDestroysTheWindowOnItsOwnThread()
    {
        var icon = new TrayNotifyIcon();
        var hwnd = icon.WindowHandle;

        var other = new Thread(icon.Dispose);
        other.Start();
        other.Join();

        // The window is destroyed once its own thread processes the request.
        PumpMessages();
        Assert.False(Functions.IsWindow(hwnd));
    }

    [Fact]
    public void ExceptionsFromEventHandlersAreReportedThroughTheThreadQueue()
    {
        var queue = Scarlet.DirectN.Threading.DirectNMessageQueue.GetOrCreateForCurrentThread();
        Exception? reported = null;
        queue.UnhandledException += (_, e) =>
        {
            reported = e.Exception;
            e.Handled = true;
        };

        try
        {
            using var icon = new TrayNotifyIcon();
            icon.Click += (_, _) => throw new FormatException("handler failure");

            // Through the real window procedure, which must not let the exception cross the native boundary.
            Functions.SendMessageW(icon.WindowHandle, TrayMessage, default, new LPARAM { Value = (nint)WM_LBUTTONUP });
            PumpMessages();

            Assert.Equal("handler failure", Assert.IsType<FormatException>(reported).Message);
        }
        finally
        {
            queue.Close();
        }
    }

    [Theory]
    [InlineData(new[] { WM_RBUTTONDOWN, WM_RBUTTONUP, WM_CONTEXTMENU })]
    [InlineData(new[] { WM_CONTEXTMENU })]
    [InlineData(new[] { NIN_KEYSELECT })]
    [InlineData(new[] { WM_LBUTTONDOWN, WM_LBUTTONUP })]
    public async Task TrayMessagesOpenTheContextMenu(uint[] messages)
    {
        using var icon = new TrayNotifyIcon { ShowContextMenuOnLeftClick = true };
        var events = new List<string>();
        icon.ContextMenu = ChoosableMenu(events);

        await ShowMenuThroughTrayMessages(icon, messages);

        Assert.Equal(["Opening", "Clicked"], events);
    }

    [Fact]
    public async Task TheReleaseOfADoubleClickDoesntOpenTheLeftClickMenu()
    {
        using var icon = new TrayNotifyIcon { ShowContextMenuOnLeftClick = true };
        var events = new List<string>();
        icon.ContextMenu = ChoosableMenu(events);
        icon.DoubleClick += (_, _) => events.Add("DoubleClick");

        // The first click's menu is chosen; the double-click's release must not open it again.
        await ShowMenuThroughTrayMessages(icon, WM_LBUTTONDOWN, WM_LBUTTONUP);
        Send(icon, WM_LBUTTONDBLCLK);
        Send(icon, WM_LBUTTONUP);

        Assert.Equal(["Opening", "Clicked", "DoubleClick"], events);
    }

    [Fact]
    public async Task DisposedWhileTheMenuIsOpenTheChosenItemDoesntRun()
    {
        var icon = new TrayNotifyIcon();
        var hwnd = icon.WindowHandle;
        var events = new List<string>();
        icon.ContextMenu = ChoosableMenu(events);

        var disposer = Task.Run(
            () =>
            {
                WaitForMenu();
                icon.Dispose();
                ChooseMenuItemByMnemonic();
            },
            TestContext.Current.CancellationToken);
        icon.ShowContextMenu();
        await disposer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        PumpMessages();

        Assert.Equal(["Opening"], events);
        Assert.False(Functions.IsWindow(hwnd));
    }

    [Fact]
    public void ExceptionsFromMenuOpeningAreReportedAndTheMenuCanOpenAgain()
    {
        var queue = Scarlet.DirectN.Threading.DirectNMessageQueue.GetOrCreateForCurrentThread();
        var reported = new List<Exception>();
        queue.UnhandledException += (_, e) =>
        {
            reported.Add(e.Exception);
            e.Handled = true;
        };

        try
        {
            using var icon = new TrayNotifyIcon();
            var menu = new TrayMenu();
            menu.Opening += (_, _) => throw new FormatException("opening failure");
            icon.ContextMenu = menu;

            Functions.SendMessageW(icon.WindowHandle, TrayMessage, default, new LPARAM { Value = (nint)WM_CONTEXTMENU });
            Functions.SendMessageW(icon.WindowHandle, TrayMessage, default, new LPARAM { Value = (nint)WM_CONTEXTMENU });
            PumpMessages();

            // Twice: the failed first attempt didn't leave the menu marked as showing.
            Assert.Equal(2, reported.Count);
            Assert.All(reported, e => Assert.Equal("opening failure", Assert.IsType<FormatException>(e).Message));
        }
        finally
        {
            queue.Close();
        }
    }

    [Fact]
    public void ClosingTheWindowFromOutsideDisposesTheIcon()
    {
        var icon = new TrayNotifyIcon();
        var hwnd = icon.WindowHandle;

        Functions.SendMessageW(hwnd, WM_CLOSE, default, default);

        Assert.False(Functions.IsWindow(hwnd));
        Assert.Throws<ObjectDisposedException>(() => icon.Visible = true);
        icon.Dispose();
    }

    [Fact]
    public void DisposeFromAnotherThreadRemovesAVisibleIconOnItsOwnThread()
    {
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        var icon = new TrayNotifyIcon { Icon = image, Visible = true };
        Assert.SkipUnless(icon.IsAdded, "No notification area (Explorer isn't running).");

        var other = new Thread(icon.Dispose);
        other.Start();
        other.Join();

        // Only the owning thread touches the icon: still there until it processes the request.
        Assert.True(icon.IsAdded);
        PumpMessages();
        Assert.False(icon.IsAdded);
        Assert.False(icon.IsInNotificationArea());
    }

    [Fact]
    public void DisposeAfterTheOwningThreadEndedRemovesTheIcon()
    {
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        TrayNotifyIcon? icon = null;
        var owner = new Thread(() => icon = new TrayNotifyIcon { Icon = image, Visible = true });
        owner.Start();
        owner.Join();
        Assert.SkipUnless(icon!.IsAdded, "No notification area (Explorer isn't running).");

        icon.Dispose();

        Assert.False(icon.IsAdded);
        Assert.False(icon.IsInNotificationArea());
    }

    [Fact]
    public void ReaddedAfterExplorerLostTheIcon()
    {
        using var image = TrayIconImage.FromStream(new MemoryStream(IcoTests.Ico.BuildReal(16)));
        using var icon = new TrayNotifyIcon { Icon = image, Visible = true };
        Assert.SkipUnless(icon.IsAdded, "No notification area (Explorer isn't running).");

        // What an Explorer restart looks like: the icon is gone, then TaskbarCreated arrives.
        var data = new NOTIFYICONDATAW { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = icon.WindowHandle, uID = icon.Id };
        Assert.True(Functions.Shell_NotifyIconW(NOTIFY_ICON_MESSAGE.NIM_DELETE, data));
        Assert.False(icon.IsInNotificationArea());

        icon.ProcessMessage(TaskbarCreatedMessage, 0, 0);

        Assert.True(icon.IsAdded);
        Assert.True(icon.IsInNotificationArea());
    }

    private static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    private static unsafe uint RegisterWindowMessage(string name)
    {
        fixed (char* p = name)
        {
            return Functions.RegisterWindowMessageW(new PWSTR { Value = (nint)p });
        }
    }

    private static void PumpMessages()
    {
        while (Functions.PeekMessageW(out var msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            Functions.TranslateMessage(msg);
            Functions.DispatchMessageW(msg);
        }
    }

    private static void ChooseMenuItemByMnemonic()
    {
        const uint WM_CHAR = 0x0102;

        // The mnemonic of "&Choose".
        Functions.PostMessageW(WaitForMenu(), WM_CHAR, new WPARAM { Value = 'c' }, default);
    }

    private static unsafe HWND WaitForMenu()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        fixed (char* className = "#32768")
        {
            while (DateTime.UtcNow < deadline)
            {
                var menuWindow = Functions.FindWindowW(new PWSTR { Value = (nint)className }, default);
                if (menuWindow.Value != 0 && Functions.IsWindowVisible(menuWindow))
                {
                    return menuWindow;
                }

                Thread.Sleep(20);
            }
        }

        throw new TimeoutException("The context menu didn't open.");
    }

    private static TrayMenu ChoosableMenu(List<string> events)
    {
        var menu = new TrayMenu { Items = { new TrayMenuItem("&Choose", (_, _) => events.Add("Clicked")) } };
        menu.Opening += (_, _) => events.Add("Opening");
        return menu;
    }

    private static async Task ShowMenuThroughTrayMessages(TrayNotifyIcon icon, params uint[] messages)
    {
        var chooser = Task.Run(ChooseMenuItemByMnemonic, TestContext.Current.CancellationToken);
        foreach (var message in messages)
        {
            Send(icon, message);
        }

        await chooser.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private static List<string> Record(TrayNotifyIcon icon)
    {
        var events = new List<string>();
        static string Describe(string name, TrayMouseEventArgs e) => $"{name}:{e.Button}:{e.Clicks}:{e.X}:{e.Y}";
        icon.MouseDown += (_, e) => events.Add(Describe("MouseDown", e));
        icon.MouseUp += (_, e) => events.Add(Describe("MouseUp", e));
        icon.MouseMove += (_, e) => events.Add(Describe("MouseMove", e));
        icon.MouseClick += (_, e) => events.Add(Describe("MouseClick", e));
        icon.MouseDoubleClick += (_, e) => events.Add(Describe("MouseDoubleClick", e));
        icon.Click += (_, _) => events.Add("Click");
        icon.DoubleClick += (_, _) => events.Add("DoubleClick");
        icon.BalloonTipShown += (_, _) => events.Add("BalloonTipShown");
        icon.BalloonTipClicked += (_, _) => events.Add("BalloonTipClicked");
        icon.BalloonTipClosed += (_, _) => events.Add("BalloonTipClosed");
        return events;
    }

    private static void Send(TrayNotifyIcon icon, uint message, int x = 0, int y = 0)
    {
        // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, HIWORD the icon id; wParam holds the anchor point.
        var wParam = (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        icon.ProcessMessage(TrayMessage, wParam, (nint)message);
    }
}
