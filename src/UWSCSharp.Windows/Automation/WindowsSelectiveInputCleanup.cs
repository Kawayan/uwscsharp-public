namespace UWSCSharp.Windows;

/// <summary>
/// 終了した実行の、未回収の入力禁止フック所有者を保持します。
/// </summary>
/// <remarks>
/// 複数の自動操作providerへ同じインスタンスを指定できます。
/// 共有するのは回収一覧だけで、実行中の入力禁止設定とID表は共有しません。
/// 未回収所有者数が0になるまで、ホスト側でこのインスタンスを保持してください。
/// 他プロセスの登録や、実行中の入力禁止を解除するAPIではありません。
/// </remarks>
public sealed class WindowsSelectiveInputCleanup
{
    internal SelectiveInputCleanupRegistry Registry { get; } = new();

    /// <summary>
    /// 未回収の所有者数を返します。個々のフックの数ではありません。
    /// </summary>
    public int PendingCount => Registry.PendingCount;

    /// <summary>
    /// 保持中の所有者へ、明示的な再回収を1回要求します。
    /// </summary>
    /// <remarks>
    /// 進行中の要求は同じTaskを共有します。途中で追加された所有者は次の要求で扱います。
    /// 各所有者の既存の停止待機予算を使い、自動で要求を繰り返しません。
    /// 複数所有者の処理全体が5秒で終了する保証ではありません。
    /// 過去の障害が残っていても回収が完了する場合があるため、
    /// 結果のErrorsとPendingCountを別々に確認してください。
    /// </remarks>
    public Task<WindowsSelectiveInputCleanupResult> RetryAsync() => Registry.RetryAsync();
}
