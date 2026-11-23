namespace UWSCSharp.Windows;

/// <summary>
/// 対象探索に必要なウィンドウ情報を提供します。
/// </summary>
internal interface ISelectiveInputWindowTree
{
    bool IsVisibleTopLevel(nint window);

    nint GetParent(nint window);

    bool HasArea(nint window);
}

/// <summary>
/// 入力位置のウィンドウから、遮断判定で比較する対象を求めます。
/// </summary>
internal static class SelectiveInputTargetResolver
{
    internal static nint Resolve(nint window, ISelectiveInputWindowTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (window == 0)
        {
            return 0;
        }

        // 原版と同じく、可視トップレベルへの到達または100段で探索を終えます。
        for (int depth = 0; depth < 100; depth++)
        {
            if (tree.IsVisibleTopLevel(window))
            {
                return window;
            }

            nint parent = tree.GetParent(window);
            if (parent == 0 || !tree.HasArea(parent))
            {
                return window;
            }

            window = parent;
        }

        return window;
    }
}
