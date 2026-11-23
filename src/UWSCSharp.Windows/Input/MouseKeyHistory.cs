namespace UWSCSharp.Windows;

/// <summary>
/// 実行内で観測したマウス押下を、消費まで保持します。
/// </summary>
internal sealed class MouseKeyHistory
{
    private static readonly int[] PhysicalKeys = [1, 2, 4];
    private int pending;

    /// <summary>
    /// 物理ボタンの非ゼロ状態を採取し、既存の保留状態へ合成します。
    /// </summary>
    internal void Poll(IKeyStateBackend backend,
        CancellationToken cancellationToken)
    {
        int observed = 0;
        foreach (int key in PhysicalKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (backend.ReadAsync(key) != 0)
            {
                observed |= key;
            }
        }

        // 取消を検出した採取結果は公開しません。
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Or(ref pending, observed);
    }

    /// <summary>
    /// 指定した物理ボタンの保留状態だけを取り出して消去します。
    /// </summary>
    internal bool Consume(int physicalKey)
    {
        if (physicalKey is not (1 or 2 or 4))
        {
            return false;
        }

        int previous = Interlocked.And(ref pending, ~physicalKey);
        return (previous & physicalKey) != 0;
    }

    /// <summary>
    /// 採取を停止した後で、保留状態をすべて消去します。
    /// </summary>
    internal void Clear() => Interlocked.Exchange(ref pending, 0);
}
