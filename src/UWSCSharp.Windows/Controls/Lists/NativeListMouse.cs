using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// LISTの項目を表示し、移動またはposted mouseを要求します。
/// </summary>
internal static class NativeListMouse
{
    internal static void Apply(nint target, int index, ClickItemFlags flags,
        IListSelectionPlatform? platform, CancellationToken token)
    {
        IListSelectionPlatform operations = platform ?? new NativeListSelectionPlatform();
        token.ThrowIfCancellationRequested();
        // 原版と同じく先頭位置を指定し、0番の行高から項目中央を計算します。
        operations.Send(target, 0x197, index, 0, true, 1000, 2);
        int top = checked((int)operations.Send(target, 0x18e, 0, 0, true, 1000, 2));
        int height = checked((int)operations.Send(target, 0x1a1, 0, 0, true, 1000, 2));
        int y = WindowsOperationErrors.Calculate(() => checked(
            checked((index - top) * height) + height / 2));
        var point = new Native.Point(4, y);
        token.ThrowIfCancellationRequested();
        if ((flags & ClickItemFlags.MouseMove) != 0)
        {
            operations.MoveCursor(operations.ToScreen(target, point));
            token.ThrowIfCancellationRequested();
        }

        if ((flags & ProviderItemMouse.Buttons) != 0)
        {
            NativeControlMouse.Post(target, (int)flags, point, operations, token);
        }
    }
}
