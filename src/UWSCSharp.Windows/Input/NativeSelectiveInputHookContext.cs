namespace UWSCSharp.Windows;

/// <summary>
/// 取得済みの可視一覧とWin32の現在位置情報で通知の対象を調べます。
/// </summary>
internal sealed class NativeSelectiveInputHookContext : ISelectiveInputHookContext
{
    private NativeSelectiveInputWindowTree tree;

    internal NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        this.tree = tree;
    }

    /// <summary>
    /// 現在の前面ウィンドウを取得します。
    /// </summary>
    public nint Foreground() => Native.GetForegroundWindow();

    /// <summary>
    /// 通知を処理する前に、左右別の修飾子とF11の保持を採取します。
    /// </summary>
    public IReadOnlyList<int> PressedEmergencyKeys()
    {
        int[] keys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C,
            UWSCSharp.Core.SelectiveInputEmergencyShortcut.Key.VirtualKey];
        return [.. keys.Where(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0)];
    }

    /// <summary>
    /// フック外の所有スレッドから一覧を置換します。取得失敗時は旧一覧を保ちます。
    /// </summary>
    internal void RefreshTargets() => tree = NativeSelectiveInputWindowTree.Capture();

    /// <summary>
    /// 物理座標の直下から、既存の親探索で対象を解決します。
    /// </summary>
    public nint ResolvePoint(int x, int y)
    {
        using var dpi = new PhysicalDpiScope();
        return SelectiveInputTargetResolver.Resolve(
            Native.WindowFromPoint(new Native.Point(x, y)), tree);
    }

    /// <summary>
    /// 通知を変更せず次のフックへ渡します。
    /// </summary>
    public nint Next(int code, nuint message, nint data) =>
        Native.CallNextHookEx(0, code, message, data);
}
