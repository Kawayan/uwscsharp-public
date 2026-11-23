namespace UWSCSharp.Windows;

[Flags]
internal enum SelectiveInputDevices
{
    None = 0,
    Keyboard = 1,
    Mouse = 2,
    All = Keyboard | Mouse
}

/// <summary>
/// 解決済みの対象と入力通知から遮断を判定します。所有スレッドからだけ呼び出します。
/// </summary>
internal sealed class SelectiveInputLockFilter
{
    private readonly nint target;
    private readonly SelectiveInputDevices devices;
    private bool allowTargetLeftUp;

    internal SelectiveInputLockFilter(nint target, SelectiveInputDevices devices)
    {
        if ((devices & ~SelectiveInputDevices.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(devices));
        }

        this.target = target;
        this.devices = devices;
    }

    /// <summary>
    /// 対象0は全体、それ以外は前面ウィンドウとの一致で判定します。
    /// </summary>
    internal bool ShouldBlockKeyboard(int code, nuint extra, nint foreground)
    {
        return code == 0 && extra != 1
            && (devices & SelectiveInputDevices.Keyboard) != 0
            && (target == 0 || target == foreground);
    }

    /// <summary>
    /// 座標から解決した対象を使います。対象指定時の移動は通します。
    /// </summary>
    internal bool ShouldBlockMouse(int code, uint message, nuint extra, nint resolvedTarget)
    {
        if (code != 0 || extra == 1 || (devices & SelectiveInputDevices.Mouse) == 0)
        {
            return false;
        }

        if (target == 0)
        {
            return true;
        }

        if (message is 0x200 or 0xa0)
        {
            return false;
        }

        if (target != resolvedTarget)
        {
            allowTargetLeftUp = true;
            return false;
        }

        // 原版の静的解析で確認した、対象外通知後の左UPを1回通す状態です。
        if (message == 0x202 && allowTargetLeftUp)
        {
            allowTargetLeftUp = false;
            return false;
        }

        return true;
    }
}
