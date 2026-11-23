namespace UWSCSharp.Core;

/// <summary>
/// 呼出し元から渡す起動引数の変更不可スナップショットです。
/// </summary>
public sealed class ScriptRunOptions
{
    private int pendingHotkeyLimit = 1024;

    /// <summary>
    /// 電源操作を実行し、再起動では解放後に通知する処理を返します。
    /// </summary>
    internal Func<PowerCommandRequest, Action?>? PowerOperation
    {
        get;
        init;
    }

    /// <summary>
    /// 内部接続用の借用演算部品です。実行完了まで解放しないでください。
    /// </summary>
    internal INumericArithmetic? NumericArithmetic
    {
        get; init;
    }

    /// <summary>
    /// ダイアログの初期フォントです。nullでは実行先の既定値を使います。
    /// フォント名とポイント数をDEFAULTFONTと同じ形式で指定し、
    /// スクリプトの初期化前に適用します。実行内のOPTIONで変更できます。
    /// </summary>
    public string? DefaultFont
    {
        get; init;
    }

    /// <summary>
    /// 実行中のProcedureを除くホットキー通知の保留上限です。
    /// </summary>
    public int PendingHotkeyLimit
    {
        get => pendingHotkeyLimit;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            pendingHotkeyLimit = value;
        }
    }

    /// <summary>
    /// CALLの相対パスを解決する主スクリプトのファイル名です。
    /// </summary>
    public string? SourcePath
    {
        get; init;
    }

    /// <summary>
    /// 任意の協調一時停止制御です。実行終了まで解放しないでください。
    /// </summary>
    public ScriptPauseControl? PauseControl
    {
        get; init;
    }

    /// <summary>
    /// 引数なしの実行設定です。
    /// </summary>
    public static ScriptRunOptions Empty { get; } = new([]);

    /// <summary>
    /// OSが分割済みのscript名と追加引数を実行設定へ変換します。
    /// </summary>
    public static ScriptRunOptions FromCommandLine(
        IReadOnlyList<string> arguments, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("scriptのパスが必要です。", nameof(arguments));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(arguments[0], nameof(arguments));
        string path = Path.GetFullPath(arguments[0], baseDirectory);
        return new ScriptRunOptions(arguments.Skip(1)) { SourcePath = path };
    }

    /// <summary>
    /// GET_APPDATA_DIRの初期値です。nullではOSのApplicationDataを使用します。
    /// 空文字を含む指定値は変換せずに公開し、ディレクトリは作成しません。
    /// </summary>
    public string? ApplicationDataDirectory
    {
        get; init;
    }

    /// <summary>
    /// GET_WIN_DIRの初期値です。nullではOSのWindowsディレクトリを使用します。
    /// 空文字を含む指定値をそのまま保持し、ディレクトリを作成しません。
    /// </summary>
    public string? WindowsDirectory
    {
        get; init;
    }

    /// <summary>
    /// GET_SYS_DIRの初期値です。nullでは実行系のシステムディレクトリを使います。
    /// 空文字を含む指定値を変換せず、フォルダーを作成しません。
    /// </summary>
    public string? SystemDirectory
    {
        get; init;
    }

    /// <summary>
    /// GET_UWSC_DIRの初期値です。nullではアプリの基底ディレクトリを使います。
    /// 空文字を含む指定値をそのまま保持し、ディレクトリを作成しません。
    /// </summary>
    public string? ExecutableDirectory
    {
        get; init;
    }

    /// <summary>
    /// 文字列を分割・変換せず、順番どおりにコピーします。
    /// </summary>
    public ScriptRunOptions(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var copied = new List<string>();
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (copied.Count == UwscArray.MaximumElements)
            {
                throw new ArgumentException(
                    "起動引数の数が配列の上限を超えました。", nameof(arguments));
            }

            copied.Add(argument);
        }

        Arguments = copied.AsReadOnly();
    }

    /// <summary>
    /// スクリプト名を含まない追加引数です。
    /// </summary>
    public IReadOnlyList<string> Arguments
    {
        get;
    }
}
