namespace UWSCSharp.Windows;

/// <summary>
/// 所有権と解放経路を障害注入で検証するためのnative境界です。
/// </summary>
internal class ClipboardApi
{
    internal virtual bool Open(nint owner) => Native.OpenClipboard(owner);

    internal virtual void Close() => Native.CloseClipboard();

    internal virtual bool Available(uint format) =>
        ClipboardBackend.IsClipboardFormatAvailable(format);

    internal virtual nint Get(uint format) => ClipboardBackend.GetClipboardData(format);

    internal virtual PixelImage ReadBitmap(nint bitmap, CancellationToken token) =>
        NativeImageCapture.ReadBitmap(bitmap, token);

    internal virtual nuint Size(nint memory) => ClipboardBackend.GlobalSize(memory);

    internal virtual nint Allocate(uint flags, nuint size) => Native.GlobalAlloc(flags, size);

    internal virtual nint Lock(nint memory) => Native.GlobalLock(memory);

    internal virtual void Unlock(nint memory) => Native.GlobalUnlock(memory);

    internal virtual void Free(nint memory) => Native.GlobalFree(memory);

    internal virtual bool Empty() => Native.EmptyClipboard();

    internal virtual nint Set(uint format, nint memory) => Native.SetClipboardData(format, memory);

    internal virtual nint CreateOwner() => Native.CreateWindowExW(0, ControlClassNames.Uppercase.Static,
        "UWSC# clipboard", 0, 0, 0, 0, 0, 0, 0, 0, 0);

    internal virtual void DestroyOwner(nint owner) => Native.DestroyWindow(owner);
}
