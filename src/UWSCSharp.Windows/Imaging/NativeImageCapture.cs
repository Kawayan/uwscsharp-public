using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal readonly record struct ImageArea(int X, int Y, int Width, int Height);

internal interface IImageCapture
{
    ImageArea Desktop
    {
        get;
    }

    PixelImage Capture(ImageArea area, CancellationToken token);
}

internal interface IWindowImageCapture
{
    PixelImage CaptureWindow(nint window, ImageArea area,
        CancellationToken token);
}

internal sealed class NativeImageCapture(
    Func<nint, nint, bool>? renderWindow = null)
    : IImageCapture, IWindowImageCapture
{
    public ImageArea Desktop => new(ImageNative.GetSystemMetrics(76),
        ImageNative.GetSystemMetrics(77), ImageNative.GetSystemMetrics(78),
        ImageNative.GetSystemMetrics(79));

    public PixelImage Capture(ImageArea area, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint dc = ImageNative.GetDC(0);
        if (dc == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "画像取得用のDCを取得できませんでした。"));
        }

        try
        {
            return CaptureFromDC(dc, area, token);
        }
        finally
        {
            ImageNative.ReleaseDC(0, dc);
        }
    }

    /// <summary>
    /// 対象の描画を取得し、ウィンドウ外周を基準に切り出します。
    /// </summary>
    public PixelImage CaptureWindow(nint window, ImageArea area,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var dpi = new PhysicalDpiScope();
        if (!Native.IsWindow(window)
            || !Native.GetWindowRect(window, out Native.Rect bounds))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "背景取得の対象ウィンドウが無効です。"));
        }

        int width = WindowsOperationErrors.Subtract(bounds.Right, bounds.Left);
        int height = WindowsOperationErrors.Subtract(bounds.Bottom, bounds.Top);
        if (width < 1 || height < 1
            || (long)width * height > PixelImage.MaximumPixels
            || area.Width < 1 || area.Height < 1 || area.X < 0 || area.Y < 0
            || (long)area.X + area.Width > width
            || (long)area.Y + area.Height > height)
        {
            throw WindowsOperationErrors.Record(
                new ArgumentException("背景取得の画像範囲が範囲外です。"));
        }

        nint dc = ImageNative.GetDC(0);
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            CheckCapture(dc != 0);
            memory = ImageNative.CreateCompatibleDC(dc);
            CheckCapture(memory != 0);
            bitmap = ImageNative.CreateCompatibleBitmap(dc, width, height);
            CheckCapture(bitmap != 0);
            previous = ImageNative.SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1)
            {
                previous = 0;
                CheckCapture(false);
            }

            CheckCapture(ImageNative.PatBlt(memory, 0, 0, width, height, 0x42));
            token.ThrowIfCancellationRequested();
            // 同期描画が終了するまでDCとbitmapを生存させます。
            CheckCapture(renderWindow is null
                ? ImageNative.PrintWindow(window, memory, 0)
                : renderWindow(window, memory));
            token.ThrowIfCancellationRequested();
            return CaptureFromDC(memory, area, token);
        }
        finally
        {
            if (previous != 0)
            {
                ImageNative.SelectObject(memory, previous);
            }

            if (bitmap != 0)
            {
                ImageNative.DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                ImageNative.DeleteDC(memory);
            }

            if (dc != 0)
            {
                ImageNative.ReleaseDC(0, dc);
            }
        }
    }

    private static void CheckCapture(bool success)
    {
        if (!success)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "背景画像の取得に失敗しました。"));
        }
    }

    internal static PixelImage ReadBitmap(nint bitmap,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ImageNative.GetObjectW(bitmap,
            Marshal.SizeOf<ImageNative.Bitmap>(), out ImageNative.Bitmap info)
                != Marshal.SizeOf<ImageNative.Bitmap>())
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "ビットマップの寸法を取得できませんでした。"));
        }

        if (info.Width < 1 || info.Height < 1
            || (long)info.Width * info.Height > PixelImage.MaximumPixels)
        {
            throw WindowsOperationErrors.Record(
                new ArgumentException("ビットマップの寸法が範囲外です。"));
        }

        nint dc = ImageNative.CreateCompatibleDC(0);
        if (dc == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "ビットマップ読取用のDCを作成できませんでした。"));
        }

        try
        {
            // クリップボード所有のbitmapは選択・解放しません。
            return ReadPixels(dc, bitmap, info.Width, info.Height, token);
        }
        finally
        {
            ImageNative.DeleteDC(dc);
        }
    }

    private static PixelImage ReadPixels(nint dc, nint bitmap,
        int width, int height, CancellationToken token)
    {
        var header = new ImageNative.BitmapHeader
        {
            Size = 40,
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32
        };
        byte[] bytes = new byte[width * height * 4];
        if (ImageNative.GetDIBits(dc, bitmap, 0, (uint)height,
            bytes, ref header, 0) != height)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        int[] pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                pixels[y * width + x] = bytes[index]
                    | bytes[index + 1] << 8 | bytes[index + 2] << 16;
            }
        }

        return new(width, height, pixels);
    }

    internal static PixelImage CaptureFromDC(nint source, ImageArea area,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (area.Width < 1 || area.Height < 1
            || (long)area.Width * area.Height > PixelImage.MaximumPixels)
        {
            throw WindowsOperationErrors.Record(
                new ArgumentException("取得画像の寸法が範囲外です。"));
        }

        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memory = ImageNative.CreateCompatibleDC(source);
            if (memory == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            bitmap = ImageNative.CreateCompatibleBitmap(source, area.Width, area.Height);
            if (bitmap == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            previous = ImageNative.SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1)
            {
                previous = 0;
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (!ImageNative.BitBlt(memory, 0, 0, area.Width, area.Height,
                source, area.X, area.Y, 0x00cc0020))
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            // GetDIBitsの呼出し前にbitmapをDCから外します。
            ImageNative.SelectObject(memory, previous);
            previous = 0;
            return ReadPixels(source, bitmap, area.Width, area.Height, token);
        }
        finally
        {
            if (previous != 0)
            {
                ImageNative.SelectObject(memory, previous);
            }

            if (bitmap != 0)
            {
                ImageNative.DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                ImageNative.DeleteDC(memory);
            }
        }
    }
}

internal static partial class ImageNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPixel;
        internal nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }
}
