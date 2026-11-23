namespace UWSCSharp.Windows;

internal static class NativeControlKey
{
    internal static void Press(nint target, int key, CancellationToken token,
        Func<nint, uint, nint, nint, nint>? sender = null)
    {
        sender ??= Native.SendMessageW;
        token.ThrowIfCancellationRequested();
        try
        {
            sender(target, 0x100, key, 0);
        }
        finally
        {
            // 取消やcallback例外があってもcontrolへkeyupを送ります。
            sender(target, 0x101, key, unchecked((nint)(int)0xc0000000));
        }

        token.ThrowIfCancellationRequested();
        sender(target, 0, 0, 0);
        token.ThrowIfCancellationRequested();
    }
}
