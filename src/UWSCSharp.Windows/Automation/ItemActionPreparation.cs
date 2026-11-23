namespace UWSCSharp.Windows;

/// <summary>
/// 項目操作の前面化を一回の要求に限定します。
/// </summary>
internal static class ItemActionPreparation
{
    internal static void Foreground(nint root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (root != 0 && Native.IsWindow(root))
        {
            nint top = Native.GetAncestor(root, 2);
            _ = Native.SetForegroundWindow(top == 0 ? root : top);
        }

        token.ThrowIfCancellationRequested();
    }
}
