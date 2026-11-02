using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scarlet.Extensions.Hosting.DirectN;
using Scarlet.Extensions.Hosting.DirectN.Threading;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon;

// A tray application without windows: it runs until "Exit" is chosen from the tray menu.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDirectN(options =>
{
    options.ShutdownMode = ShutdownMode.OnExplicitShutdown;
});
builder.Services.AddDirectNLifetime();
builder.Services.AddThreadSwitching();
builder.Services.AddDirectNTrayIcon<TrayIcon>();

await builder.Build().RunAsync();

/// <summary>
/// The tray icon: left or right click opens the menu; Exit stops the host.
/// </summary>
internal sealed class TrayIcon(IDirectNThread thread, JoinableTaskFactory jtf) : ITrayIcon
{
    private TrayNotifyIcon? _notifyIcon;
    private TrayIconImage? _image;
    private TrayMenuItem? _statusItem;

    public void InitializeComponent()
    {
        _image = TrayIconImage.FromApplicationIcon();
        _statusItem = new TrayMenuItem("Idle") { Enabled = false };

        var menu = new TrayMenu
        {
            Items =
            {
                new TrayMenuItem(ApplicationInfo.ProductVersion) { Enabled = false },
                _statusItem,
                TrayMenuItem.Separator(),
                new TrayMenuItem("Do &work", (_, _) => _ = DoWorkAsync()),
                new TrayMenuItem("E&xit", (_, _) => thread.HandleApplicationExit()) { IsDefault = true },
            },
        };

        _notifyIcon = new TrayNotifyIcon
        {
            Icon = _image,
            Text = $"Scarlet tray sample {ApplicationInfo.ProductVersion}",
            ShowContextMenuOnLeftClick = true,
            ContextMenu = menu,
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) => _notifyIcon.ShowContextMenu();
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
        _image?.Dispose();
    }

    private async Task DoWorkAsync()
    {
        _statusItem!.Text = "Working...";

        // Off the UI thread for the work, back on it to update the icon.
        await TaskScheduler.Default;
        await Task.Delay(TimeSpan.FromSeconds(2));
        await jtf.SwitchToMainThreadAsync();

        _statusItem.Text = $"Last run {DateTime.Now:T}";
        _notifyIcon?.ShowBalloonTip("Scarlet tray sample", "The work is done.", TrayToolTipIcon.Info);
    }
}
