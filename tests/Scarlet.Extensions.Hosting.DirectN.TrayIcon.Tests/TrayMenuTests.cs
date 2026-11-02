using System.Runtime.InteropServices;
using DirectN;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;
using static Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal.NativeMethods;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Tests;

public partial class TrayMenuTests
{
    [Fact]
    public unsafe void BuildsANativeMenuWithStatesSeparatorsAndSubmenus()
    {
        var exit = new TrayMenuItem("E&xit");
        var version = new TrayMenuItem("1.0") { Enabled = false };
        var option = new TrayMenuItem("Option") { Checked = true, IsDefault = true };
        var child = new TrayMenuItem("Child");
        var parent = new TrayMenuItem("More") { Items = { child } };
        var menu = new TrayMenu { Items = { version, TrayMenuItem.Separator(), option, parent, exit } };

        using var native = NativeMenu.Create(menu);

        Assert.Equal(5, GetMenuItemCount(native.Handle.Value));
        Assert.Equal(5, native.Commands.Count);
        Assert.Same(exit, native.Commands.Values.Last());
        Assert.Contains(child, native.Commands.Values);

        var info = GetItem(native.Handle, 0);
        Assert.Equal(MFS_DISABLED, (uint)info.fState & MFS_DISABLED);

        info = GetItem(native.Handle, 1);
        Assert.Equal(MFT_SEPARATOR, (uint)info.fType & MFT_SEPARATOR);

        info = GetItem(native.Handle, 2);
        Assert.Equal(MFS_CHECKED | MFS_DEFAULT, (uint)info.fState & (MFS_CHECKED | MFS_DEFAULT));
        Assert.Same(option, native.FindCommand(info.wID));

        info = GetItem(native.Handle, 3);
        Assert.NotEqual(0, info.hSubMenu.Value);
        Assert.Equal(1, GetMenuItemCount(info.hSubMenu.Value));
    }

    [Fact]
    public void UnknownCommandsAreIgnored()
    {
        using var native = NativeMenu.Create(new TrayMenu { Items = { new TrayMenuItem("Only") } });

        Assert.Null(native.FindCommand(0));
        Assert.Null(native.FindCommand(999));
    }

    [Fact]
    public void ItemConstructorWiresClickAndPerformClickRaisesIt()
    {
        var clicked = 0;
        var item = new TrayMenuItem("Item", (_, _) => clicked++);

        item.PerformClick();

        Assert.Equal(1, clicked);
        Assert.True(item.Enabled);
        Assert.False(item.IsSeparator);
        Assert.True(TrayMenuItem.Separator().IsSeparator);
    }

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(nint menu);

    private static unsafe MENUITEMINFOW GetItem(HMENU menu, uint position)
    {
        var info = new MENUITEMINFOW
        {
            cbSize = (uint)sizeof(MENUITEMINFOW),
            fMask = (MENU_ITEM_MASK)(MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU),
        };
        Assert.True(Functions.GetMenuItemInfoW(menu, position, true, ref info));
        return info;
    }
}
