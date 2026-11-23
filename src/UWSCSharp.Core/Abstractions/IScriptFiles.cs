namespace UWSCSharp.Core;

/// <summary>
/// 実行ごとに独立したファイル管理を作成します。
/// </summary>
public interface IScriptFiles
{
    /// <summary>
    /// 独立したファイルID表を持つセッションを作成します。
    /// </summary>
    IScriptFileSession CreateSession();
}

/// <summary>
/// ファイルIDと終了時のcloseを管理します。
/// </summary>
public interface IScriptFileSession : IAsyncDisposable
{
    /// <summary>
    /// この実行の相対パスの基準です。既定はプロセスの現在値です。
    /// </summary>
    string CurrentDirectory => Environment.CurrentDirectory;

    /// <summary>
    /// 直前のGETDIRで取得した名前のスナップショットです。
    /// </summary>
    IReadOnlyList<string> DirectoryFiles => [];

    /// <summary>
    /// ファイル関数を実行します。
    /// </summary>
    ValueTask<Value> InvokeAsync(string name,
        IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// ファイル関数の引数と採用範囲を検証します。
/// </summary>
public static class FileFunctions
{
    private static readonly Dictionary<string, (int Min, int Max)>
        Signatures = new(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltinFunctionNames.FOpen] = (1, 2),
            [BuiltinFunctionNames.FClose] = (1, 2),
            [BuiltinFunctionNames.FGet] = (2, 4),
            [BuiltinFunctionNames.FPut] = (2, 5),
            [BuiltinFunctionNames.FDelLine] = (2, 2),
            [BuiltinFunctionNames.ReadIni] = (2, 3),
            [BuiltinFunctionNames.WriteIni] = (3, 4),
            [BuiltinFunctionNames.DeleteIni] = (1, 3),
            [BuiltinFunctionNames.GetDir] = (1, 4),
            [BuiltinFunctionNames.DeleteFile] = (1, 1)
        };

    /// <summary>
    /// 対応するファイル関数か判定します。
    /// </summary>
    public static bool IsBuiltin(string name) =>
        Signatures.ContainsKey(name);

    internal static (int Min, int Max) GetSignature(string name) =>
        Signatures[name];

    /// <summary>
    /// 副作用の前に引数と未対応指定を検査します。
    /// </summary>
    public static void Validate(string name,
        IReadOnlyList<Value> arguments)
    {
        string function = name.ToUpperInvariant();
        (int Min, int Max) signature = GetSignature(function);
        BuiltinFunctions.CheckArguments(name, arguments,
            signature.Min, signature.Max);
        if (function == BuiltinFunctionNames.FOpen)
        {
            _ = OpenFileArguments(arguments);
            return;
        }

        if (function == BuiltinFunctionNames.FPut)
        {
            _ = WriteFileArguments(arguments);
            return;
        }

        if (function == BuiltinFunctionNames.FGet)
        {
            _ = ReadFileArguments(arguments);
            return;
        }

        if (function == BuiltinFunctionNames.FClose)
        {
            _ = CloseFileId(arguments[0]);
            _ = CloseSuppress(arguments);
            return;
        }

        if (function == BuiltinFunctionNames.FDelLine)
        {
            _ = FileLineArgument(arguments[0]);
            _ = FileLineArgument(arguments[1]);
            return;
        }

        if (function == BuiltinFunctionNames.DeleteFile)
        {
            _ = DeleteFilePath(arguments[0]);
            return;
        }

        if (function == BuiltinFunctionNames.GetDir)
        {
            string path = BuiltinText.Convert(arguments[0]);
            string pattern = arguments.Count < 2 || arguments[1].Kind == ValueKind.Omitted
                ? "*" : BuiltinText.Convert(arguments[1]);
            if (path.Length == 0 || path.Contains('\0') || pattern.Contains('\0')
                || (pattern != "\\" && (pattern.IndexOfAny(['/', '\\', ':', '<', '>', '|', '"']) >= 0
                    || pattern.Any(character => character < ' '))))
            {
                throw new InvalidOperationException("GETDIRのパスまたはファイル名指定が不正です。");
            }

            var args = new CallArguments(arguments);
            _ = NumericOperations.RuntimeNumber(args.Get(2));
            _ = DirectoryOrder(args.Get(3));
            return;
        }

        if (function is BuiltinFunctionNames.ReadIni or BuiltinFunctionNames.WriteIni
            or BuiltinFunctionNames.DeleteIni)
        {
            foreach (Value argument in arguments)
            {
                if (argument.Kind != ValueKind.Omitted
                    && BuiltinText.Convert(argument).Contains('\0'))
                {
                    throw new InvalidOperationException("INIの引数にNULは指定できません。");
                }
            }

            string sectionInput = BuiltinText.Convert(arguments[0]);
            string section = sectionInput.Trim();
            string keyInput = arguments.Count < 2 || arguments[1].Kind == ValueKind.Omitted
                ? "" : BuiltinText.Convert(arguments[1]);
            string key = keyInput.Trim();
            bool delete = function == BuiltinFunctionNames.DeleteIni;
            if (section.Length == 0 || (key.Length == 0 && (!delete || keyInput.Length > 0))
                || sectionInput.IndexOfAny(['[', ']', '\r', '\n']) >= 0
                || keyInput.IndexOfAny(['=', '\r', '\n']) >= 0
                || ((delete || function == BuiltinFunctionNames.WriteIni)
                    && (key.StartsWith(';') || key.StartsWith('#') || key.StartsWith('[')))
                || (function == BuiltinFunctionNames.WriteIni
                    && BuiltinText.Convert(arguments[2]).IndexOfAny(['\r', '\n']) >= 0))
            {
                throw new InvalidOperationException("INIの名前が不正です。");
            }

            return;
        }

        throw new NotSupportedException(name);
    }

    internal static (string Path, int Mode) OpenFileArguments(
        IReadOnlyList<Value> arguments, string? baseDirectory = null)
    {
        string path = BuiltinText.Convert(arguments[0]);
        if (path.Length == 0 || path.Any(character => character < ' '))
        {
            throw new InvalidOperationException("FOPENのパスが不正です。");
        }

        try
        {
            path = Path.GetFullPath(path,
                baseDirectory ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("FOPENのパスが不正です。", ex);
        }

        double number = arguments.Count < 2
            || arguments[1].Kind == ValueKind.Omitted
            ? (int)ScriptFileMode.Read
            : Math.Truncate(NumericOperations.RuntimeNumber(arguments[1]));
        if (number < 1 || number > int.MaxValue)
        {
            throw new InvalidOperationException("FOPENのモードが範囲外です。");
        }

        int mode = (int)number;
        int write = mode & (int)ScriptFileMode.WriteMask;
        if (mode != (int)ScriptFileMode.Exists
            && ((mode & ~(int)ScriptFileMode.SupportedMask) != 0
                || (mode & (int)ScriptFileMode.AccessMask) == 0
                || (write != 0 && (write & (write - 1)) != 0)))
        {
            throw new InvalidOperationException("未対応または不正なFOPENモードです。");
        }

        return (path, mode);
    }

    internal static int DirectoryOrder(Value value)
    {
        double order = Math.Truncate(NumericOperations.RuntimeNumber(value));
        if (order is < 0 or > 4)
        {
            throw new InvalidOperationException("GETDIRの取得順番が範囲外です。");
        }

        return (int)order;
    }

    internal static int FileLineArgument(Value value)
    {
        double number = Math.Truncate(NumericOperations.RuntimeNumber(value));
        if (number < 1 || number > int.MaxValue)
        {
            throw new InvalidOperationException("FDELLINEのIDと行は正のInt32範囲で指定してください。");
        }

        return (int)number;
    }

    internal static int CloseFileId(Value value)
    {
        double number = Math.Truncate(
            NumericOperations.RuntimeNumber(value));
        if (number < int.MinValue || number > int.MaxValue)
        {
            throw new InvalidOperationException(
                "FCLOSEのIDはInt32範囲で指定してください。");
        }

        return (int)number;
    }

    internal static (int Id, int Row, int Column, int QuoteMode)
        ReadFileArguments(IReadOnlyList<Value> arguments)
    {
        int Integer(int index)
        {
            if (index >= arguments.Count
                || arguments[index].Kind == ValueKind.Omitted)
            {
                return 0;
            }

            double number = Math.Truncate(
                NumericOperations.RuntimeNumber(arguments[index]));
            if (number < int.MinValue || number > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "FGETの整数引数はInt32範囲で指定してください。");
            }

            return (int)number;
        }

        int id = Integer(0);
        int row = Integer(1);
        int column = Integer(2);
        int quoteMode = Integer(3);
        if (id < 1 || row is 0 or < -2 || column < 0
            || quoteMode is < 0 or > 2)
        {
            throw new InvalidOperationException(
                "FGETのID・行・列・引用符指定が範囲外です。");
        }

        return (id, row, column, quoteMode);
    }

    internal static bool CloseSuppress(IReadOnlyList<Value> arguments)
    {
        return arguments.Count > 1
            && arguments[1].Kind != ValueKind.Omitted
            && Math.Truncate(NumericOperations.RuntimeNumber(
                arguments[1])) != 0;
    }

    internal static (int Id, string Text, int Row, int Column,
        int QuoteMode) WriteFileArguments(IReadOnlyList<Value> arguments)
    {
        int Integer(int index)
        {
            if (index >= arguments.Count
                || arguments[index].Kind == ValueKind.Omitted)
            {
                return 0;
            }

            double number = Math.Truncate(
                NumericOperations.RuntimeNumber(arguments[index]));
            if (number < int.MinValue || number > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "FPUTの整数引数はInt32範囲で指定してください。");
            }

            return (int)number;
        }

        int id = Integer(0);
        string text = BuiltinText.Convert(arguments[1]);
        int row = Integer(2);
        int column = Integer(3);
        int quoteMode = Integer(4);
        if (id < 1 || (row < 0 && row != -2) || column < -1
            || (column == -1 && row <= 0)
            || quoteMode is < 0 or > 3)
        {
            throw new InvalidOperationException(
                "FPUTのID・行・列・引用符指定が範囲外です。");
        }

        return (id, text, row, column, quoteMode);
    }

    internal static string DeleteFilePath(Value value,
        string? baseDirectory = null)
    {
        string path = BuiltinText.Convert(value);
        if (path.Length == 0 || path.Any(character => character < ' ')
            || path.IndexOfAny(['<', '>', '|', '"']) >= 0)
        {
            throw new InvalidOperationException("DELETEFILEのパスが不正です。");
        }

        try
        {
            string fullPath = Path.GetFullPath(path,
                baseDirectory ?? Environment.CurrentDirectory);
            string? directory = Path.GetDirectoryName(fullPath);
            string pattern = Path.GetFileName(fullPath);
            if (directory is null || directory.IndexOfAny(['*', '?']) >= 0
                || pattern.Length == 0 || pattern.Contains(':'))
            {
                throw new InvalidOperationException("DELETEFILEの指定は最後のファイル名に限定してください。");
            }

            return fullPath;
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("DELETEFILEのパスが不正です。", error);
        }
    }
}
