using System.ComponentModel;
using System.Runtime.InteropServices;
using DirectN;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// An icon for a <see cref="TrayNotifyIcon"/>. Except for <see cref="FromHandle"/>, the image is loaded at the size the notification
/// area needs for the DPI at the time the icon is added or updated (and again when Explorer restarts or recreates the taskbar).
/// </summary>
/// <remarks>A <see cref="TrayNotifyIcon"/> doesn't own its image: dispose the image after the icon.</remarks>
public sealed unsafe class TrayIconImage : IDisposable
{
    private readonly Func<int, nint>? _load;
    private readonly nint _handle;
    private readonly bool _ownsHandle;
    private readonly Dictionary<int, nint> _loaded = [];
    private bool _disposed;

    private TrayIconImage(Func<int, nint> load)
    {
        _load = load;
    }

    private TrayIconImage(nint handle, bool ownsHandle)
    {
        _handle = handle;
        _ownsHandle = ownsHandle;
    }

    /// <summary>
    /// The executable's application icon (<c>&lt;ApplicationIcon&gt;</c> in the project file).
    /// </summary>
    /// <returns>The image.</returns>
    public static TrayIconImage FromApplicationIcon() => FromResource(NativeMethods.ApplicationIconResourceId);

    /// <summary>
    /// An icon group resource of the executable.
    /// </summary>
    /// <param name="resourceId">The resource id.</param>
    /// <returns>The image.</returns>
    public static TrayIconImage FromResource(int resourceId) => new(size =>
        Functions.LoadImageW(
            new HINSTANCE(Functions.GetModuleHandleW(default).Value),
            new PWSTR { Value = resourceId },
            GDI_IMAGE_TYPE.IMAGE_ICON,
            size,
            size,
            0).Value);

    /// <summary>
    /// An .ico file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The image.</returns>
    public static TrayIconImage FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Icon file not found.", fullPath);
        }

        return new(size =>
        {
            fixed (char* p = fullPath)
            {
                return Functions.LoadImageW(default, new PWSTR { Value = (nint)p }, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE).Value;
            }
        });
    }

    /// <summary>
    /// An .ico file read from a stream, e.g. an embedded resource. The stream is read immediately and not kept.
    /// </summary>
    /// <param name="ico">The .ico content.</param>
    /// <returns>The image.</returns>
    public static TrayIconImage FromStream(Stream ico)
    {
        ArgumentNullException.ThrowIfNull(ico);
        using var buffer = new MemoryStream();
        ico.CopyTo(buffer);
        var bytes = buffer.ToArray();

        // Validate every image now rather than when one is first needed, at whatever size the DPI asks for.
        foreach (var image in IcoReader.ReadDirectory(bytes))
        {
            var handle = CreateIcon(bytes, image.Data, image.Width);
            if (handle == 0)
            {
                throw new InvalidDataException($"The .ico file has an invalid {image.Width}x{image.Width} image.");
            }

            Functions.DestroyIcon(new HICON { Value = handle });
        }

        return new(size => CreateIcon(bytes, IcoReader.SelectImage(bytes, size), size));
    }

    private static nint CreateIcon(byte[] ico, Range image, int size)
    {
        var (offset, length) = image.GetOffsetAndLength(ico.Length);
        fixed (byte* p = &ico[offset])
        {
            return NativeMethods.CreateIconFromResourceEx(p, (uint)length, 1, 0x00030000, size, size, 0);
        }
    }

    /// <summary>
    /// An existing icon handle (<c>HICON</c>), used as is at every size.
    /// </summary>
    /// <param name="handle">The icon handle.</param>
    /// <param name="ownsHandle">Whether disposing the image destroys the handle.</param>
    /// <returns>The image.</returns>
    public static TrayIconImage FromHandle(nint handle, bool ownsHandle)
    {
        if (handle == 0)
        {
            throw new ArgumentException("The icon handle is null.", nameof(handle));
        }

        return new(handle, ownsHandle);
    }

    /// <summary>
    /// Gets the icon handle for a size in physical pixels, loading it on first use. The image keeps ownership of the handle.
    /// </summary>
    /// <param name="size">The width and height in physical pixels.</param>
    /// <returns>The icon handle.</returns>
    public nint GetHandle(int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_load is null)
        {
            return _handle;
        }

        lock (_loaded)
        {
            if (_loaded.TryGetValue(size, out var handle))
            {
                return handle;
            }

            handle = _load(size);
            if (handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The icon could not be loaded.");
            }

            _loaded[size] = handle;
            return handle;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_loaded)
        {
            foreach (var handle in _loaded.Values)
            {
                Functions.DestroyIcon(new HICON { Value = handle });
            }

            _loaded.Clear();
        }

        if (_ownsHandle)
        {
            Functions.DestroyIcon(new HICON { Value = _handle });
        }
    }
}
