namespace UWSCSharp.Wpf;

internal sealed class TrayLauncherClickState
{
    private long? pending;
    private long? suppressedUntil;

    internal bool Click(long now, int doubleClickMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(doubleClickMilliseconds);
        if (suppressedUntil is long end && now < end)
        {
            return false;
        }

        suppressedUntil = null;
        pending = now + doubleClickMilliseconds;
        return true;
    }

    internal void DoubleClick(long now, int doubleClickMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(doubleClickMilliseconds);
        pending = null;
        // ダブルクリック後に届く2回目のボタン解放を単クリックへ戻さない。
        suppressedUntil = now + doubleClickMilliseconds;
    }

    internal bool TakePending(long now)
    {
        if (pending is not long end || now < end)
        {
            return false;
        }

        pending = null;
        return true;
    }

    internal void Cancel()
    {
        pending = null;
        suppressedUntil = null;
    }
}
