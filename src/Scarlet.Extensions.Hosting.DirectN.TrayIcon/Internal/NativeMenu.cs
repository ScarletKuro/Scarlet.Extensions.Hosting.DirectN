using System.ComponentModel;
using System.Runtime.InteropServices;
using DirectN;
using static Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal.NativeMethods;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;

/// <summary>
/// A native popup menu built from a <see cref="TrayMenu"/>, with command ids mapped back to the items.
/// </summary>
internal sealed unsafe class NativeMenu : IDisposable
{
    private readonly Dictionary<uint, TrayMenuItem> _commands = [];
    private uint _nextId = 1;

    private NativeMenu(IList<TrayMenuItem> items)
    {
        Handle = Build(items);
    }

    public HMENU Handle { get; private set; }

    public IReadOnlyDictionary<uint, TrayMenuItem> Commands => _commands;

    public static NativeMenu Create(TrayMenu menu) => new(menu.Items);

    public TrayMenuItem? FindCommand(uint id) => _commands.GetValueOrDefault(id);

    public void Dispose()
    {
        // Destroys the submenus too.
        if (Handle.Value != 0)
        {
            Functions.DestroyMenu(Handle);
            Handle = default;
        }
    }

    internal static uint GetState(TrayMenuItem item) =>
        (item.Enabled ? 0 : MFS_DISABLED) | (item.Checked ? MFS_CHECKED : 0) | (item.IsDefault ? MFS_DEFAULT : 0);

    private HMENU Build(IList<TrayMenuItem> items)
    {
        var menu = Functions.CreatePopupMenu();
        if (menu.Value == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            uint position = 0;
            foreach (var item in items)
            {
                Insert(menu, position++, item);
            }
        }
        catch
        {
            Functions.DestroyMenu(menu);
            throw;
        }

        return menu;
    }

    private void Insert(HMENU menu, uint position, TrayMenuItem item)
    {
        var info = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW) };
        if (item.IsSeparator)
        {
            info.fMask = (MENU_ITEM_MASK)MIIM_FTYPE;
            info.fType = (MENU_ITEM_TYPE)MFT_SEPARATOR;
            InsertItem(menu, position, info);
            return;
        }

        info.fMask = (MENU_ITEM_MASK)(MIIM_FTYPE | MIIM_STRING | MIIM_STATE | MIIM_ID);
        info.fType = (MENU_ITEM_TYPE)MFT_STRING;
        info.fState = (MENU_ITEM_STATE)GetState(item);
        info.wID = _nextId++;
        _commands[info.wID] = item;

        if (item.Items.Count > 0)
        {
            info.fMask |= (MENU_ITEM_MASK)MIIM_SUBMENU;
            info.hSubMenu = Build(item.Items);
        }

        var text = item.Text ?? string.Empty;
        fixed (char* p = text)
        {
            info.dwTypeData = new PWSTR { Value = (nint)p };
            info.cch = (uint)text.Length;
            try
            {
                InsertItem(menu, position, info);
            }
            catch
            {
                if (info.hSubMenu.Value != 0)
                {
                    Functions.DestroyMenu(info.hSubMenu);
                }

                throw;
            }
        }
    }

    private static void InsertItem(HMENU menu, uint position, in MENUITEMINFOW info)
    {
        if (!Functions.InsertMenuItemW(menu, position, true, info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}
