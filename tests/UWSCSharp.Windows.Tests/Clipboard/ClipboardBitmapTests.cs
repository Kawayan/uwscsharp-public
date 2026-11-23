using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ClipboardBitmapTests
{
    private sealed class Api : ClipboardApi
    {
        internal string Failure = "";
        internal int Closes;
        internal Action? AfterGet;

        internal override bool Open(nint owner) => Failure != "open";

        internal override void Close() => Closes++;

        internal override bool Available(uint format)
        {
            return format == 2 && Failure != "missing";
        }

        internal override nint Get(uint format)
        {
            Assert.Equal(2u, format);
            AfterGet?.Invoke();
            return Failure == "get" ? 0 : 42;
        }

        internal override PixelImage ReadBitmap(nint bitmap,
            CancellationToken token)
        {
            Assert.Equal((nint)42, bitmap);
            Assert.Equal(0, Closes);
            if (Failure == "read")
            {
                throw new Win32Exception("読取失敗");
            }

            return new(1, 1, [0x123456]);
        }

        internal override nint Lock(nint memory) =>
            throw new InvalidOperationException("bitmapはGlobalLockしません。");

        internal override void Free(nint memory) =>
            throw new InvalidOperationException("借用したbitmapは解放しません。");
    }

    [Theory]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("missing")]
    [InlineData("get")]
    [InlineData("read")]
    [InlineData("cancel")]
    public void ReadClosesOpenedClipboardAndBorrowsBitmap(string failure)
    {
        var api = new Api { Failure = failure };
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancel")
        {
            api.AfterGet = cancellation.Cancel;
        }

        var backend = new ClipboardBackend(api);
        if (failure == "")
        {
            Assert.Equal(0x123456,
                backend.ReadImage(cancellation.Token).Pixels[0]);
        }
        else if (failure == "missing")
        {
            Assert.Throws<InvalidOperationException>(() =>
                backend.ReadImage(cancellation.Token));
        }
        else if (failure == "cancel")
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                backend.ReadImage(cancellation.Token));
        }
        else
        {
            Assert.Throws<Win32Exception>(() =>
                backend.ReadImage(cancellation.Token));
        }

        Assert.Equal(failure == "open" ? 0 : 1, api.Closes);
    }

    [Fact]
    public void NativeReadPreservesPixelsAndBorrowedBitmap()
    {
        var header = new ImageNative.BitmapHeader
        {
            Size = 40,
            Width = 2,
            Height = -2,
            Planes = 1,
            BitCount = 32
        };
        nint bitmap = CreateDIBSection(0, ref header, 0,
            out nint bits, 0, 0);
        Assert.NotEqual(0, bitmap);
        try
        {
            int[] pixels = [0x123456, 0xabcdef, 0xfedcba, 0x102030];
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Assert.Equal(pixels,
                NativeImageCapture.ReadBitmap(bitmap, default).Pixels);
            Assert.Equal(Marshal.SizeOf<ImageNative.Bitmap>(),
                ImageNative.GetObjectW(bitmap,
                    Marshal.SizeOf<ImageNative.Bitmap>(), out _));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeImageCapture.ReadBitmap(bitmap, cancellation.Token));
        }
        finally
        {
            Assert.True(ImageNative.DeleteObject(bitmap));
        }

        Assert.Throws<Win32Exception>(() =>
            NativeImageCapture.ReadBitmap(0, default));
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc,
        ref ImageNative.BitmapHeader header, uint usage,
        out nint bits, nint section, uint offset);
}
