namespace UWSCSharp.IO;

/// <summary>
/// 明示ログの保存先と既定ログの保存容量を管理します。
/// </summary>
public interface IScriptLogStoragePolicy
{
    /// <summary>
    /// 設定を変更する前に、解決済みの明示パスを検査します。
    /// </summary>
    ValueTask ValidateExplicitPathAsync(string path,
        CancellationToken cancellationToken);

    /// <summary>
    /// 一時ファイルの作成から置換までの排他を取得します。
    /// 保存できない場合は例外を返し、既存ファイルを保持します。
    /// </summary>
    ValueTask<IDisposable> AcquireDefaultSaveAsync(string path,
        long prospectiveBytes, CancellationToken cancellationToken);
}
