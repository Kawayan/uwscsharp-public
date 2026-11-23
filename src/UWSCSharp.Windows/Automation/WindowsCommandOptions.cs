namespace UWSCSharp.Windows;

/// <summary>
/// ホストが指定するコマンド出力と表示起動の設定です。
/// </summary>
public sealed record WindowsCommandOptions
{
    /// <summary>
    /// 標準出力と標準エラーの合計取得上限です。
    /// </summary>
    public int MaximumOutputBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// ANSI出力のコードページです。省略時はWindowsのACPを使います。
    /// </summary>
    public int? AnsiCodePage
    {
        get; init;
    }

    /// <summary>
    /// 表示起動に使う、対応した実行元の起動情報です。
    /// </summary>
    public WindowsCommandHost? CommandHost
    {
        get; init;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumOutputBytes);
        if (AnsiCodePage is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AnsiCodePage));
        }

        if (AnsiCodePage is int codePage)
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
            _ = System.Text.Encoding.GetEncoding(codePage);
        }
    }
}
