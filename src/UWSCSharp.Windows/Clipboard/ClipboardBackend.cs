using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal interface IClipboardBackend
{
    string ReadText(CancellationToken token);

    void WriteText(string text, CancellationToken token);

    PixelImage ReadImage(CancellationToken token);

    void WriteImage(PixelImage image, CancellationToken token);
}

internal interface IClipboardImageFormats
{
    void WriteImage(PixelImage image, int depth, CancellationToken token);
}

/// <summary>
/// clipboardのロックと転送メモリの所有権を管理します。
/// </summary>
internal sealed partial class ClipboardBackend(ClipboardApi? native = null)
    : IClipboardBackend, IClipboardImageFormats
{
    private const uint BitmapFormat = 2;
    private readonly ClipboardApi api = native ?? new ClipboardApi();

    public string ReadText(CancellationToken token)
    {
        byte[]? data = Read(13, token);
        if (data is null)
        {
            return "";
        }

        if ((data.Length & 1) != 0)
        {
            throw WindowsOperationErrors.Record(
                new InvalidDataException("clipboardの文字列サイズが不正です。"));
        }

        string text = Encoding.Unicode.GetString(data);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    public void WriteText(string text, CancellationToken token) =>
        Write(13, Encoding.Unicode.GetBytes(text + '\0'), token);

    public PixelImage ReadImage(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!api.Open(0))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        byte[]? data = null;
        try
        {
            foreach (uint format in new uint[] { 8, 17 })
            {
                if (api.Available(format))
                {
                    data = Copy(format, token);
                    break;
                }
            }

            if (data is null)
            {
                if (!api.Available(BitmapFormat))
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("clipboardにbitmap画像がありません。"));
                }

                nint bitmap = api.Get(BitmapFormat);
                token.ThrowIfCancellationRequested();
                if (bitmap == 0)
                {
                    throw WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error(),
                            "clipboardのbitmapを取得できませんでした。"));
                }

                // OS所有のbitmapは、clipboardを閉じる前にコピーします。
                PixelImage image = api.ReadBitmap(bitmap, token);
                token.ThrowIfCancellationRequested();
                return image;
            }
        }
        finally
        {
            api.Close();
        }

        return ClipboardDib.Decode(data, token);
    }

    public void WriteImage(PixelImage image, CancellationToken token) =>
        Write(8, EncodeDib(image, token), token);

    public void WriteImage(PixelImage image, int depth, CancellationToken token)
    {
        if (depth == 24)
        {
            WriteImage(image, token);
            return;
        }

        byte[] data = BitmapEncoding.Encode(image, depth, token).PackedDib();
        token.ThrowIfCancellationRequested();
        Write(8, data, token);
    }

    internal static byte[] EncodeDib(PixelImage image, CancellationToken token)
    {
        int stride = (image.Width * 3 + 3) & ~3;
        byte[] data = new byte[checked(40 + stride * image.Height)];
        BinaryPrimitives.WriteInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), image.Height);
        data[12] = 1;
        data[14] = 24;
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++)
            {
                int color = image[x, y];
                int index = 40 + (image.Height - y - 1) * stride + x * 3;
                data[index] = (byte)color;
                data[index + 1] = (byte)(color >> 8);
                data[index + 2] = (byte)(color >> 16);
            }
        }

        return data;
    }

    private byte[]? Read(uint format, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!api.Open(0))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if (!api.Available(format))
            {
                return null;
            }

            return Copy(format, token);
        }
        finally
        {
            api.Close();
        }
    }

    private byte[] Copy(uint format, CancellationToken token)
    {
        nint memory = api.Get(format);
        if (memory == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        nuint size = api.Size(memory);
        if (size == 0 || size > 134217728)
        {
            throw WindowsOperationErrors.Record(
                new InvalidDataException("clipboardのデータサイズが範囲外です。"));
        }

        nint pointer = api.Lock(memory);
        if (pointer == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            byte[] data = new byte[(int)size];
            Marshal.Copy(pointer, data, 0, data.Length);
            token.ThrowIfCancellationRequested();
            return data;
        }
        finally
        {
            api.Unlock(memory);
        }
    }

    private void Write(uint format, byte[] data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint memory = api.Allocate(2, (nuint)data.Length);
        if (memory == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        nint owner = 0;
        bool opened = false;
        try
        {
            nint pointer = api.Lock(memory);
            if (pointer == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                Marshal.Copy(data, 0, pointer, data.Length);
            }
            finally
            {
                api.Unlock(memory);
            }

            owner = api.CreateOwner();
            if (owner == 0 || !api.Open(owner))
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            opened = true;
            token.ThrowIfCancellationRequested();
            if (!api.Empty() || api.Set(format, memory) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            memory = 0;
        }
        finally
        {
            if (opened)
            {
                api.Close();
            }

            if (owner != 0)
            {
                api.DestroyOwner(owner);
            }

            if (memory != 0)
            {
                api.Free(memory);
            }
        }

        token.ThrowIfCancellationRequested();
    }
}
