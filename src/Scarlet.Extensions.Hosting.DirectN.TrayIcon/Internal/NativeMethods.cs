using System.Runtime.InteropServices;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;

/// <summary>
/// The few Win32 calls DirectNAot doesn't generate. Source-generated, AOT-safe.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_USER = 0x0400;

    public const uint NIN_SELECT = WM_USER + 0;
    public const uint NIN_KEYSELECT = NIN_SELECT | 0x1;
    public const uint NIN_BALLOONSHOW = WM_USER + 2;
    public const uint NIN_BALLOONHIDE = WM_USER + 3;
    public const uint NIN_BALLOONTIMEOUT = WM_USER + 4;
    public const uint NIN_BALLOONUSERCLICK = WM_USER + 5;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MIIM_STATE = 0x0001;
    public const uint MIIM_ID = 0x0002;
    public const uint MIIM_SUBMENU = 0x0004;
    public const uint MIIM_STRING = 0x0040;
    public const uint MIIM_FTYPE = 0x0100;
    public const uint MFT_STRING = 0x0000;
    public const uint MFT_SEPARATOR = 0x0800;
    public const uint MFS_DISABLED = 0x0003;
    public const uint MFS_CHECKED = 0x0008;
    public const uint MFS_DEFAULT = 0x1000;

    public const int SM_CXSMICON = 49;
    public const int SM_MENUDROPALIGNMENT = 40;

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;

    // The resource id the .NET SDK gives the <ApplicationIcon> icon group (IDI_APPLICATION).
    public const int ApplicationIconResourceId = 32512;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreateIconFromResourceEx(byte* presbits, uint dwResSize, int fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    public const uint MSGFLT_ALLOW = 1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    public static short LoWord(nint value) => unchecked((short)(long)value);

    public static short HiWord(nint value) => unchecked((short)((long)value >> 16));
}
