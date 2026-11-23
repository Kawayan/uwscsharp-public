using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class JpegWriter
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInput
    {
        internal uint Version;
        internal nint DebugCallback;
        internal int SuppressThread;
        internal int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EncoderParameter
    {
        internal Guid Id;
        internal uint Count;
        internal uint Type;
        internal nint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EncoderParameters
    {
        internal uint Count;
        internal EncoderParameter Parameter;
    }

    internal static void Save(PixelImage image, string path, int quality, CancellationToken token)
    {
        if (quality is < 1 or > 100)
        {
            throw WindowsOperationErrors.Record(
                new ArgumentOutOfRangeException(nameof(quality)));
        }

        ImageFileWriter.Write(path, temporary => Encode(image, temporary, quality, token), token);
    }

    private static void Encode(PixelImage image, string path, int quality, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = new StartupInput { Version = 1, SuppressExternalCodecs = 1 };
        Check(GdiplusStartup(out nuint startup, ref input, 0));
        nint bitmap = 0;
        nint pixels = 0;
        nint parameterValue = 0;
        try
        {
            int byteCount = checked(image.Width * image.Height * 4);
            pixels = Marshal.AllocHGlobal(byteCount);
            int[] row = new int[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < image.Width; x++)
                {
                    row[x] = image[x, y] | unchecked((int)0xff000000);
                }

                Marshal.Copy(row, 0, pixels + y * image.Width * 4, row.Length);
            }

            Check(GdipCreateBitmapFromScan0(image.Width, image.Height,
                image.Width * 4, 0x26200a, pixels, out bitmap));
            parameterValue = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(parameterValue, quality);
            var parameters = new EncoderParameters
            {
                Count = 1,
                Parameter = new EncoderParameter
                {
                    Id = new("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb"),
                    Count = 1,
                    Type = 4,
                    Value = parameterValue
                }
            };
            var jpeg = new Guid("557cf401-1a04-11d3-9a73-0000f81ef32e");
            token.ThrowIfCancellationRequested();
            Check(GdipSaveImageToFile(bitmap, path, ref jpeg, ref parameters));
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            if (bitmap != 0)
            {
                GdipDisposeImage(bitmap);
            }

            if (parameterValue != 0)
            {
                Marshal.FreeHGlobal(parameterValue);
            }

            if (pixels != 0)
            {
                Marshal.FreeHGlobal(pixels);
            }

            GdiplusShutdown(startup);
        }
    }

    internal static void Check(int status)
    {
        if (status != 0)
        {
            var failure = new InvalidOperationException(
                $"GDI+画像処理に失敗しました（status={status}）。");
            // GDI+のメモリ不足は一般の操作エラーにしません。
            throw status == 3 ? failure : WindowsOperationErrors.Record(failure);
        }
    }
}
