using System.Buffers.Binary;
using Scarlet.Extensions.Hosting.DirectN.TrayIcon.Internal;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon.Tests;

public class IcoTests
{
    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 24)]
    [InlineData(24, 24)]
    [InlineData(25, 32)]
    [InlineData(40, 256)]
    [InlineData(300, 256)]
    public void SelectsTheSmallestImageAtLeastAsLargeElseTheLargest(int size, int expectedWidth)
    {
        var ico = Ico.Build((16, 32), (24, 32), (32, 32), (256, 32));

        var range = IcoReader.SelectImage(ico, size);

        Assert.Equal(expectedWidth, Ico.WidthOf(ico, range));
    }

    [Fact]
    public void PrefersMoreColorsAtTheSameSize()
    {
        var ico = Ico.Build((16, 8), (16, 32), (16, 4));

        var range = IcoReader.SelectImage(ico, 16);

        Assert.Equal(32, Ico.BitsOf(ico, range));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0, 0, 2, 0, 1, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0, 16, 16 })]
    public void RejectsInvalidFiles(byte[] ico)
    {
        Assert.Throws<InvalidDataException>(() => IcoReader.SelectImage(ico, 16));
    }

    [Fact]
    public void RejectsImagesOutsideTheFile()
    {
        var ico = Ico.Build((16, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 12), (uint)ico.Length);

        Assert.Throws<InvalidDataException>(() => IcoReader.SelectImage(ico, 16));
    }

    [Fact]
    public void FromStreamCreatesARealIconAndCachesItPerSize()
    {
        using var image = TrayIconImage.FromStream(new MemoryStream(Ico.BuildReal(16)));

        var small = image.GetHandle(16);
        Assert.NotEqual(0, small);
        Assert.Equal(small, image.GetHandle(16));
        Assert.NotEqual(0, image.GetHandle(32));
    }

    [Fact]
    public void FromStreamRejectsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => TrayIconImage.FromStream(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public void FromFileRequiresAnExistingFile()
    {
        Assert.Throws<FileNotFoundException>(() => TrayIconImage.FromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico")));
        Assert.Throws<ArgumentException>(() => TrayIconImage.FromFile(string.Empty));
    }

    [Fact]
    public void FromFileLoadsTheIconAtTheRequestedSize()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico");
        File.WriteAllBytes(path, Ico.BuildReal(32));
        try
        {
            using var image = TrayIconImage.FromFile(path);

            Assert.NotEqual(0, image.GetHandle(16));
            Assert.NotEqual(0, image.GetHandle(32));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingResourceFailsWhenItIsFirstNeeded()
    {
        using var image = TrayIconImage.FromResource(12345);

        Assert.Throws<System.ComponentModel.Win32Exception>(() => image.GetHandle(16));
    }

    [Fact]
    public void FromApplicationIconUsesTheExecutablesIconResource()
    {
        // This test executable has an <ApplicationIcon>.
        using var image = TrayIconImage.FromApplicationIcon();

        Assert.NotEqual(0, image.GetHandle(16));
        Assert.NotEqual(0, image.GetHandle(32));
    }

    [Fact]
    public unsafe void FromHandleUsesTheHandleAtEverySizeAndOwnsItWhenAsked()
    {
        var ico = Ico.BuildReal(16);
        var range = IcoReader.SelectImage(ico, 16);
        var (offset, length) = range.GetOffsetAndLength(ico.Length);
        nint handle;
        fixed (byte* p = &ico[offset])
        {
            handle = NativeMethods.CreateIconFromResourceEx(p, (uint)length, 1, 0x00030000, 16, 16, 0);
        }

        Assert.NotEqual(0, handle);

        var image = TrayIconImage.FromHandle(handle, ownsHandle: true);
        Assert.Equal(handle, image.GetHandle(16));
        Assert.Equal(handle, image.GetHandle(48));
        image.Dispose();
        image.Dispose();

        // Owned: destroyed by Dispose, so destroying it again fails.
        Assert.False(global::DirectN.Functions.DestroyIcon(new global::DirectN.HICON { Value = handle }));
        Assert.Throws<ArgumentException>(() => TrayIconImage.FromHandle(0, ownsHandle: false));
    }

    [Fact]
    public void DisposedImageThrows()
    {
        var image = TrayIconImage.FromStream(new MemoryStream(Ico.BuildReal(16)));
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.GetHandle(16));
    }

    /// <summary>
    /// Builds .ico files.
    /// </summary>
    internal static class Ico
    {
        /// <summary>
        /// A directory whose image data is a placeholder (enough for <see cref="IcoReader"/>).
        /// </summary>
        public static byte[] Build(params (int Width, int Bits)[] images)
        {
            const int DataSize = 8;
            var directorySize = 6 + (16 * images.Length);
            var ico = new byte[directorySize + (DataSize * images.Length)];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)images.Length);
            for (var i = 0; i < images.Length; i++)
            {
                var entry = ico.AsSpan(6 + (16 * i), 16);
                entry[0] = (byte)(images[i].Width == 256 ? 0 : images[i].Width);
                entry[1] = entry[0];
                BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)images[i].Bits);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], DataSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(directorySize + (DataSize * i)));
            }

            return ico;
        }

        /// <summary>
        /// A valid single-image 32bpp .ico file.
        /// </summary>
        public static byte[] BuildReal(int size)
        {
            var pixels = size * size * 4;
            var maskStride = ((size + 31) / 32) * 4;
            var imageSize = 40 + pixels + (maskStride * size);
            var ico = new byte[6 + 16 + imageSize];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
            var entry = ico.AsSpan(6, 16);
            entry[0] = (byte)size;
            entry[1] = (byte)size;
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)imageSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 22);

            // BITMAPINFOHEADER; the height covers the color image and the AND mask.
            var header = ico.AsSpan(22, 40);
            BinaryPrimitives.WriteInt32LittleEndian(header, 40);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
            ico.AsSpan(62, pixels).Fill(0xFF);
            return ico;
        }

        public static int WidthOf(byte[] ico, Range range) => EntryOf(ico, range)[0] is 0 ? 256 : EntryOf(ico, range)[0];

        public static int BitsOf(byte[] ico, Range range) => BinaryPrimitives.ReadUInt16LittleEndian(EntryOf(ico, range)[6..]);

        private static ReadOnlySpan<byte> EntryOf(byte[] ico, Range range)
        {
            var offset = range.Start.Value;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
            for (var i = 0; i < count; i++)
            {
                var entry = ico.AsSpan(6 + (16 * i), 16);
                if (BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) == offset)
                {
                    return entry;
                }
            }

            throw new InvalidOperationException("No entry for the range.");
        }
    }
}
