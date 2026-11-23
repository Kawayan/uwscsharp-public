using System.Buffers.Binary;
using System.ComponentModel;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class BitmapEncodingTests
{
    private sealed class Api : BitmapConversionApi
    {
        internal string Failure = "";
        internal Action? AfterCreate;
        internal int Reads;
        internal int Deletes;
        internal int Releases;

        internal override nint GetDC() => Failure == "dc" ? 0 : 1;

        internal override void ReleaseDC(nint dc)
        {
            Assert.Equal((nint)1, dc);
            Releases++;
        }

        internal override nint CreateBitmap(nint dc, PixelImage image,
            nint pixels)
        {
            Assert.NotEqual(0, pixels);
            AfterCreate?.Invoke();
            return Failure == "create" ? 0 : 2;
        }

        internal override int ReadBitmap(nint dc, nint bitmap, int height,
            byte[] pixels, byte[] info)
        {
            Reads++;
            return Failure == "read" ? 0 : height;
        }

        internal override void DeleteBitmap(nint bitmap)
        {
            Assert.Equal((nint)2, bitmap);
            Deletes++;
            if (Failure == "delete")
            {
                throw new InvalidOperationException("解放失敗");
            }
        }
    }

    [Theory]
    [InlineData("dc", 0, 0, 0)]
    [InlineData("create", 0, 0, 1)]
    [InlineData("read", 1, 1, 1)]
    [InlineData("delete", 1, 1, 1)]
    [InlineData("", 1, 1, 1)]
    public void EveryOwnedResourceIsReleased(string failure,
        int reads, int deletes, int releases)
    {
        var api = new Api { Failure = failure };
        var image = new PixelImage(1, 1, [0]);
        if (failure == "")
        {
            Assert.Single(ClipboardDib.Decode(BitmapEncoding.Encode(
                image, 8, default, api).PackedDib(), default).Pixels);
        }
        else if (failure == "delete")
        {
            Assert.Throws<InvalidOperationException>(() =>
                BitmapEncoding.Encode(image, 8, default, api));
        }
        else
        {
            Assert.Throws<Win32Exception>(() =>
                BitmapEncoding.Encode(image, 8, default, api));
        }

        Assert.Equal(reads, api.Reads);
        Assert.Equal(deletes, api.Deletes);
        Assert.Equal(releases, api.Releases);
    }

    [Fact]
    public void CancellationAfterBitmapCreationReleasesBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new Api { AfterCreate = cancellation.Cancel };
        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(() =>
            BitmapEncoding.Encode(new(1, 1, [0]), 16, cancellation.Token, api));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(0, api.Reads);
        Assert.Equal(1, api.Deletes);
        Assert.Equal(1, api.Releases);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void NativeConversionPreservesBlackWhiteAndRows(int depth)
    {
        var image = new PixelImage(3, 2,
            [0, 0xffffff, 0, 0xffffff, 0, 0xffffff]);
        BitmapEncoding.Data data = BitmapEncoding.Encode(image, depth, default);
        Assert.Equal(depth, BinaryPrimitives.ReadInt16LittleEndian(data.Info.AsSpan(14)));
        Assert.Equal(image.Pixels, ClipboardDib.Decode(data.PackedDib(), default).Pixels);
        int rowBytes = (image.Width * depth + 7) / 8;
        int stride = (rowBytes + 3) / 4 * 4;
        for (int y = 0; y < image.Height; y++)
        {
            Assert.All(data.Pixels.AsSpan(y * stride + rowBytes,
                stride - rowBytes).ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void NativeRgbConversionUsesMatchingHeaderAndPixels(int depth)
    {
        var image = new PixelImage(3, 1, [0x123456, 0xabcdef, 0xffffff]);
        PixelImage decoded = ClipboardDib.Decode(
            BitmapEncoding.Encode(image, depth, default).PackedDib(), default);
        int tolerance = depth == 16 ? 8 : 0;
        for (int index = 0; index < image.Pixels.Length; index++)
        {
            foreach (int shift in new[] { 0, 8, 16 })
            {
                Assert.InRange(Math.Abs((image.Pixels[index] >> shift & 255)
                    - (decoded.Pixels[index] >> shift & 255)), 0, tolerance);
            }
        }
    }

    [Theory]
    [InlineData("image", false, "image.bmp")]
    [InlineData("image.jpg", false, "image.jpg.bmp")]
    [InlineData("image.BMP", false, "image.BMP")]
    [InlineData("image.", false, "image.bmp")]
    [InlineData("image", true, "image.jpg")]
    [InlineData("image.bmp", true, "image.bmp.jpg")]
    [InlineData("image.JPG", true, "image.JPG")]
    [InlineData("image.jpe", true, "image.jpe")]
    [InlineData("image.jpeg", true, "image.jpeg")]
    [InlineData("image.", true, "image.jpg")]
    public void ExtensionMatchesTheSelectedFormat(string path,
        bool jpeg, string expected) =>
        Assert.Equal(expected, ImageSavePath.Resolve(path, jpeg));
}
