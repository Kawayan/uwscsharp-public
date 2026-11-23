namespace UWSCSharp.Windows;

internal interface IButtonAccessibleRoutePlatform
{
    bool Available();

    Native.Rect Bounds(nint window);

    int ExtendedStyle(nint window);

    nint Parent(nint window);

    nint Foreground();

    bool Iconic(nint window);

    int Metric(int index);

    nint PointWindow(Native.Point point, CancellationToken token);

    bool ReadPoint(Native.Point point, ref int role, out int state,
        CancellationToken token);

    void ReadRoot(nint root, Native.Point point, ref int role, out int state,
        CancellationToken token);

    bool ActRoot(nint root, Native.Point point, CancellationToken token);
}

/// <summary>
/// BTN補助の座標・状態取得と操作切替条件をまとめます。
/// </summary>
internal static class NativeButtonAccessibleRoute
{
    internal static int Read(nint root, nint target,
        IButtonAccessibleRoutePlatform platform, CancellationToken token,
        ref int role, out bool accessibleAction)
    {
        accessibleAction = false;
        token.ThrowIfCancellationRequested();
        if (!platform.Available())
        {
            token.ThrowIfCancellationRequested();
            return -1;
        }

        Native.Rect bounds = platform.Bounds(target);
        var point = new Native.Point(bounds.Left, bounds.Top);
        nint comparisonRoot = root;
        nint mdiParent = 0;
        if ((platform.ExtendedStyle(root) & 0x40) != 0)
        {
            mdiParent = platform.Parent(root);
            for (int level = 0; level < 2; level++)
            {
                token.ThrowIfCancellationRequested();
                // 原本は存在確認と代入で親を別々に取得します。
                if (platform.Parent(mdiParent) != 0)
                {
                    mdiParent = platform.Parent(mdiParent);
                }
            }

            if (mdiParent != 0)
            {
                comparisonRoot = mdiParent;
            }
        }

        token.ThrowIfCancellationRequested();
        if (platform.Foreground() == comparisonRoot && OnScreen(point, platform))
        {
            bool found = platform.ReadPoint(point, ref role, out int directState, token);
            token.ThrowIfCancellationRequested();
            if (found)
            {
                return directState;
            }
        }

        platform.ReadRoot(root, point, ref role, out int state, token);
        token.ThrowIfCancellationRequested();
        accessibleAction = platform.Iconic(comparisonRoot)
            || !OnScreen(point, platform)
            || (platform.Foreground() != comparisonRoot
                && platform.PointWindow(point, token) != comparisonRoot);
        token.ThrowIfCancellationRequested();
        if (!accessibleAction && mdiParent != 0)
        {
            Native.Rect parentBounds = platform.Bounds(comparisonRoot);
            accessibleAction = platform.Iconic(root)
                || point.X < parentBounds.Left || point.X > parentBounds.Right
                || point.Y < parentBounds.Top || point.Y > parentBounds.Bottom;
            token.ThrowIfCancellationRequested();
        }

        return state;
    }

    internal static bool Act(nint root, nint target,
        IButtonAccessibleRoutePlatform platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Native.Rect bounds = platform.Bounds(target);
        var point = new Native.Point(WindowsOperationErrors.Add(bounds.Left, bounds.Right) / 2,
            WindowsOperationErrors.Add(bounds.Top, bounds.Bottom) / 2);
        token.ThrowIfCancellationRequested();
        bool result = platform.ActRoot(root, point, token);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static bool OnScreen(Native.Point point, IButtonAccessibleRoutePlatform platform)
        => platform.Metric(76) <= point.X
            && WindowsOperationErrors.Add(platform.Metric(76), platform.Metric(78)) >= point.X
            && platform.Metric(77) <= point.Y
            && WindowsOperationErrors.Add(platform.Metric(77), platform.Metric(79)) >= point.Y;
}
