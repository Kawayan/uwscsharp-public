namespace UWSCSharp.Windows;

internal static class NativeTreeSelection
{
    internal static nint SelectMatch(nint root, nint target, nint item,
        uint flags, bool back, IListSelectionPlatform platform, CancellationToken token,
        bool callerSelection = false)
    {
        nint accepted = Select(target, item, platform, token);
        if ((flags & 0x20) == 0)
        {
            var keys = new NativeListSelectionBackend(root, target,
                combo: false, back: back, platform: platform);
            keys.PressKey(107, token);
            accepted = Select(target, item, platform, token);
        }

        // パスの2要素目以降はcaller側でも同じ項目を選択します。
        return callerSelection ? Select(target, item, platform, token) : accepted;
    }

    internal static nint Select(nint target, nint item,
        IListSelectionPlatform platform, CancellationToken token)
    {
        // 原版の準備対象はrootではなく、選択先のTREEです。
        var preparation = new NativeListSelectionBackend(target, target,
            combo: false, back: true, platform: platform);
        preparation.PrepareRoot(token);
        nint accepted = platform.Send(target, 0x110b, 9, item, false, 0, 0);
        token.ThrowIfCancellationRequested();

        // 原版68a5e4(1)は10msを10回待ち、message処理とyield2を行います。
        for (int index = 0; index < 10; index++)
        {
            preparation.Sleep(10, token);
        }

        platform.Pump(token);
        token.ThrowIfCancellationRequested();
        preparation.Yield(2, token);
        preparation.Yield(20, token);
        preparation.PrepareRoot(token);
        return accepted;
    }
}
