namespace UWSCSharp.Windows;

internal static class NativeControlMouse
{
    internal static void Post(nint target, int kind, Native.Point point,
        IListSelectionPlatform platform, CancellationToken token)
    {
        // 原版はXを下位WORDへ切り詰め、YにはWORDの範囲検査を行います。
        uint packed = unchecked((ushort)point.X) | ((uint)checked((ushort)point.Y) << 16);
        nint data = unchecked((nint)packed);
        bool right = (kind & 4096) != 0;
        uint up = right ? 0x205u : 0x202u;
        token.ThrowIfCancellationRequested();
        try
        {
            platform.Priority(0x100);
            SendPair(right ? 0x204u : 0x201u);
            if ((kind & 8192) != 0)
            {
                SendPair(right ? 0x206u : 0x203u);
            }
        }
        finally
        {
            // 送信中の取消や例外でも原版の通常priorityへの復帰を要求します。
            platform.Priority(0x20);
        }

        void SendPair(uint down)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                platform.Post(target, down, 0, data);
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                // downの通知中に取消があってもupを要求します。
                platform.Post(target, up, 0, data);
            }

            token.ThrowIfCancellationRequested();
            if (right)
            {
                // 原版の右ボタン経路はupを続けて2回postします。
                platform.Post(target, up, 0, data);
                token.ThrowIfCancellationRequested();
            }
        }
    }
}
