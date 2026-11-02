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

        Assert.Equal(["MouseDown:Left:1:10:20", "MouseUp:Left:1:10:20", "Click", "MouseClick:Left:1:10:20"], events);
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
                "MouseDown:Left:1:0:0", "MouseUp:Left:1:0:0", "Click", "MouseClick:Left:1:0:0",
                "MouseDoubleClick:Left:2:0:0", "DoubleClick", "MouseDown:Left:2:0:0", "MouseUp:Left:1:0:0",
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

        Assert.Equal([$"MouseDown:{button}:1:0:0", $"MouseUp:{button}:1:0:0", "Click", $"MouseClick:{button}:1:0:0"], events);
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
