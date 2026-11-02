using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DirectN;
using Scarlet.DirectN.Threading;

namespace Scarlet.Extensions.Hosting.DirectN.Internal;

/// <summary>
/// A hidden top-level window on the UI thread that receives the end of the Windows session (log off, shut down, restart, or
/// the Restart Manager closing the application). Message-only windows don't receive those broadcasts.
/// </summary>
internal sealed unsafe partial class SessionEndWindow : IDisposable
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;

    // Unique per copy of this library in the process, so each copy has its own window procedure.
    private static readonly string ClassName = "Scarlet.Extensions.Hosting.DirectN.SessionEnd." + Guid.NewGuid().ToString("N");
    private static readonly Lock ClassRegistrationLock = new();
    private static bool s_classRegistered;

    private readonly Action _onSessionEnd;
    private GCHandle _self;
    private HWND _hwnd;

    /// <param name="onSessionEnd">
    /// Called on the UI thread when the end of the session is confirmed. Windows may end the process once it returns.
    /// </param>
    public SessionEndWindow(Action onSessionEnd)
    {
        _onSessionEnd = onSessionEnd;
        EnsureClassRegistered();
        _self = GCHandle.Alloc(this);
        fixed (char* className = ClassName)
        {
            _hwnd = Functions.CreateWindowExW(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                new PWSTR { Value = (nint)className },
                default,
                0,
                0, 0, 0, 0,
                default,
                default,
                new HINSTANCE { Value = Functions.GetModuleHandleW(default).Value },
                0);
        }

        if (_hwnd.Value == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _self.Free();
            throw new Win32Exception(error);
        }

        Functions.SetWindowLongPtrW(_hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, GCHandle.ToIntPtr(_self));
    }

    internal HWND Handle => _hwnd;

    /// <summary>
    /// Destroys the window. On the UI thread.
    /// </summary>
    public void Dispose()
    {
        if (_hwnd.Value == 0)
        {
            return;
        }

        Functions.SetWindowLongPtrW(_hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
        Functions.DestroyWindow(_hwnd);
        _hwnd = default;
        _self.Free();
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassRegistrationLock)
        {
            if (s_classRegistered)
            {
                return;
            }

            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSW
                {
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                    hInstance = new HINSTANCE { Value = Functions.GetModuleHandleW(default).Value },
                    lpszClassName = new PWSTR { Value = (nint)className },
                };

                if (Functions.RegisterClassW(wc) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_QUERYENDSESSION)
        {
            // Never veto the end of the session.
            return 1;
        }

        if (msg == WM_ENDSESSION)
        {
            var userData = Functions.GetWindowLongPtrW(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
            if (wParam != 0 && userData != 0 && GCHandle.FromIntPtr(userData).Target is SessionEndWindow window)
            {
                try
                {
                    window._onSessionEnd();
                }
#pragma warning disable CA1031 // Exceptions must not cross the native boundary.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    DirectNMessageQueue.RethrowAsUnhandled(exception);
                }
            }

            return 0;
        }

        return Functions.DefWindowProcW(new HWND(hwnd), msg, new WPARAM { Value = (nuint)wParam }, new LPARAM { Value = lParam }).Value;
    }
}
