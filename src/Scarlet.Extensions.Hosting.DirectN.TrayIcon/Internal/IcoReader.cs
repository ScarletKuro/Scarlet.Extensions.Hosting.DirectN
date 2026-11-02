using System.Buffers.Binary;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;

/// <summary>
/// Reads the directory of an .ico file and picks the image best suited for a size.
/// </summary>
internal static class IcoReader
{
    private const int HeaderSize = 6;
    private const int EntrySize = 16;

    /// <summary>
    /// Picks the image for <paramref name="size"/>: the smallest one at least that large (so it only scales down), preferring more
    /// colors; else the largest one.
    /// </summary>
    /// <returns>The image data range (BMP DIB or PNG) inside <paramref name="ico"/>.</returns>
    /// <exception cref="InvalidDataException">Not a valid .ico file.</exception>
    public static Range SelectImage(ReadOnlySpan<byte> ico, int size)
    {
        var images = ReadDirectory(ico);
        var best = images[0];
        foreach (var image in images)
        {
            if (IsBetter(image.Width, image.Bits, best.Width, best.Bits, size))
            {
                best = image;
            }
        }

        return best.Data;
    }

    /// <summary>
    /// Reads every directory entry, checking that each image lies inside the file.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a valid .ico file.</exception>
    public static IcoImage[] ReadDirectory(ReadOnlySpan<byte> ico)
    {
        if (ico.Length < HeaderSize
            || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            throw new InvalidDataException("Not an .ico file.");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        if (count == 0 || ico.Length < HeaderSize + (count * EntrySize))
        {
            throw new InvalidDataException("The .ico file has no images or a truncated directory.");
        }

        var images = new IcoImage[count];
        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(HeaderSize + (i * EntrySize), EntrySize);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (length == 0 || offset > (uint)ico.Length || length > (uint)ico.Length - offset)
            {
                throw new InvalidDataException("The .ico file has an image outside the file.");
            }

            images[i] = new IcoImage(
                entry[0] == 0 ? 256 : entry[0],
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
                new Range((int)offset, (int)(offset + length)));
        }

        return images;
    }

    private static bool IsBetter(int width, int bits, int bestWidth, int bestBits, int size)
    {
        var fits = width >= size;
        var bestFits = bestWidth >= size;
        if (fits != bestFits)
        {
            return fits;
        }

        if (width != bestWidth)
        {
            // Both large enough: the smaller one; both too small: the larger one.
            return fits ? width < bestWidth : width > bestWidth;
        }

        return bits > bestBits;
    }
}

/// <summary>
/// An image in an .ico file.
/// </summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Bits">The bits per pixel.</param>
/// <param name="Data">The image data range (BMP DIB or PNG) in the file.</param>
internal readonly record struct IcoImage(int Width, int Bits, Range Data);
