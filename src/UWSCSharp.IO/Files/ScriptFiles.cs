using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// テキストファイルを実行単位でバッファ管理します。
/// </summary>
public sealed partial class ScriptFiles : IScriptFiles
{
    private readonly string? defaultIniPath;
    private readonly string? baseDirectory;
    private readonly DirectoryEnumerator enumerateDirectory =
        static (path, pattern, options) =>
            new DirectoryInfo(path).EnumerateFileSystemInfos(pattern, options);
    private readonly Action<string> deleteFile = File.Delete;

    /// <summary>
    /// 新規UTF-8出力だけを許可し、保存時も既存ファイルを上書きしません。
    /// </summary>
    public bool NewOutputOnly
    {
        get; init;
    }

    /// <summary>
    /// INIの既定パスを指定しないadapterを作成します。
    /// </summary>
    public ScriptFiles()
    {
    }

    /// <summary>
    /// 列挙元を指定して、途中の取消や失敗を検証します。
    /// </summary>
    internal ScriptFiles(DirectoryEnumerator enumerateDirectory)
    {
        ArgumentNullException.ThrowIfNull(enumerateDirectory);
        this.enumerateDirectory = enumerateDirectory;
    }

    /// <summary>
    /// 個々のファイル削除を指定して、削除後の取消を検証します。
    /// </summary>
    internal ScriptFiles(Action<string> deleteFile)
    {
        ArgumentNullException.ThrowIfNull(deleteFile);
        this.deleteFile = deleteFile;
    }

    /// <summary>
    /// スクリプト名と作業ディレクトリからINIの既定パスを決めます。
    /// </summary>
    public ScriptFiles(string scriptPath, string? workingDirectory = null)
        : this(scriptPath, workingDirectory, null)
    {
    }

    private ScriptFiles(string scriptPath, string? workingDirectory,
        string? baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        defaultIniPath = Path.GetFullPath(Path.Combine(
            workingDirectory ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(scriptPath) + ".INI"));
        this.baseDirectory = baseDirectory;
    }

    /// <summary>
    /// 主スクリプトのフォルダーを基準とするファイル操作を作成します。
    /// </summary>
    public static ScriptFiles ForScript(string scriptPath,
        bool newOutputOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        string path = Path.GetFullPath(scriptPath);
        string directory = Path.GetDirectoryName(path)!;
        return new ScriptFiles(path, directory, directory)
        {
            NewOutputOnly = newOutputOnly
        };
    }

    /// <summary>
    /// ファイルIDを共有しないセッションを作成します。
    /// </summary>
    public IScriptFileSession CreateSession() =>
        new Session(defaultIniPath, NewOutputOnly, baseDirectory,
            enumerateDirectory, deleteFile);

    private sealed class Entry(string path, int mode,
        List<string> lines, FileStream? lease = null,
        Encoding? automaticEncoding = null)
    {
        internal string Path { get; } = path;

        internal int Mode { get; } = mode;

        internal List<string> Lines { get; set; } = lines;

        internal FileStream? Lease { get; } = lease;

        internal Encoding? AutomaticEncoding { get; } = automaticEncoding;
    }

    private sealed class Session(string? defaultIniPath, bool newOutputOnly,
        string? baseDirectory, DirectoryEnumerator enumerateDirectory,
        Action<string> deleteFile) : IScriptFileSession
    {
        private readonly Dictionary<int, Entry> entries = [];
        private int nextId = 1;
        private bool disposed;

        /// <summary>
        /// 固定した主ファイルの基準、または現在の作業フォルダーです。
        /// </summary>
        public string CurrentDirectory
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return baseDirectory ?? Environment.CurrentDirectory;
            }
        }

        public IReadOnlyList<string> DirectoryFiles { get; private set; } = [];

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            FileFunctions.Validate(name, arguments);
            var args = new CallArguments(arguments);
            string IniPath(int i, bool finiteText = false)
            {
                string path = finiteText ? BuiltinText.Convert(args.Get(i)) : args.Str(i);
                if (path.Length == 0)
                {
                    return defaultIniPath ?? throw new InvalidOperationException(
                        "INIファイル名の省略にはスクリプト名の指定が必要です。");
                }

                return Path.GetFullPath(Path.HasExtension(path)
                    ? path : path + ".INI",
                    baseDirectory ?? Environment.CurrentDirectory);
            }

            Value result;
            switch (name.ToUpperInvariant())
            {
                case BuiltinFunctionNames.ReadIni:
                    return ReadIniAsync(IniPath(2, true),
                        BuiltinText.Convert(args.Get(0)).Trim(),
                        BuiltinText.Convert(args.Get(1)).Trim(), cancellationToken);
                case BuiltinFunctionNames.WriteIni:
                    return UpdateIniAsync(IniPath(3, true),
                        BuiltinText.Convert(args.Get(0)).Trim(),
                        BuiltinText.Convert(args.Get(1)).Trim(),
                        BuiltinText.Convert(args.Get(2)), cancellationToken);
                case BuiltinFunctionNames.DeleteIni:
                    return UpdateIniAsync(IniPath(2, true),
                        BuiltinText.Convert(args.Get(0)).Trim(),
                        BuiltinText.Convert(args.Get(1)).Trim(), null, cancellationToken);
                case BuiltinFunctionNames.GetDir:
                    DirectoryFiles = ListDirectory(BuiltinText.Convert(args.Get(0)),
                        BuiltinText.Convert(args.Get(1, Value.String("*"))),
                        Math.Truncate(NumericOperations.RuntimeNumber(args.Get(2))) != 0,
                        FileFunctions.DirectoryOrder(args.Get(3)), cancellationToken, enumerateDirectory,
                        baseDirectory);
                    result = Value.Numeric(DirectoryFiles.Count);
                    break;
                case BuiltinFunctionNames.DeleteFile:
                    result = Value.Logical(DeleteFiles(
                        FileFunctions.DeleteFilePath(args.Get(0), baseDirectory),
                        cancellationToken, deleteFile));
                    break;
                case BuiltinFunctionNames.FDelLine:
                    DeleteLine(Find(FileFunctions.FileLineArgument(args.Get(0))),
                        FileFunctions.FileLineArgument(args.Get(1)), cancellationToken);
                    result = Value.Empty;
                    break;
                case BuiltinFunctionNames.FOpen:
                    (string path, int mode) = FileFunctions.OpenFileArguments(arguments, baseDirectory);
                    return OpenAsync(path, mode, cancellationToken);
                case BuiltinFunctionNames.FClose:
                    result = Value.Logical(Close(
                        FileFunctions.CloseFileId(args.Get(0)),
                        FileFunctions.CloseSuppress(arguments)));
                    break;
                case BuiltinFunctionNames.FGet:
                    (int id, int row, int column, int quoteMode) =
                        FileFunctions.ReadFileArguments(arguments);
                    result = Get(Find(id), row, column, quoteMode,
                        cancellationToken);
                    break;
                case BuiltinFunctionNames.FPut:
                    (int putId, string text, int putRow,
                        int putColumn, int putMode) =
                        FileFunctions.WriteFileArguments(arguments);
                    Entry entry = Find(putId);
                    if ((entry.Mode & (int)ScriptFileMode.WriteMask) == 0)
                    {
                        throw new InvalidOperationException(
                            "読み専用ファイルには書き込めません。");
                    }

                    Put(entry, text, putRow, putColumn, putMode,
                        cancellationToken);
                    result = Value.Empty;
                    break;
                default:
                    throw new NotSupportedException(name);
            }

            return ValueTask.FromResult(result);
        }

        private Entry Find(int id) =>
            entries.TryGetValue(id, out Entry? entry) ? entry
            : throw new InvalidOperationException("ファイルIDが無効です。");

        private static void DeleteLine(Entry entry, int row, CancellationToken token)
        {
            if ((entry.Mode & (int)ScriptFileMode.WriteMask) == 0 || row > entry.Lines.Count)
            {
                throw new InvalidOperationException("FDELLINEのモードまたは削除行が無効です。");
            }

            token.ThrowIfCancellationRequested();
            var lines = new List<string>(entry.Lines.Count - 1);
            for (int i = 0; i < entry.Lines.Count; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (i != row - 1)
                {
                    lines.Add(entry.Lines[i]);
                }
            }

            token.ThrowIfCancellationRequested();
            entry.Lines = lines;
        }

        private async ValueTask<Value> OpenAsync(string path, int mode,
            CancellationToken token)
        {
            if (mode == (int)ScriptFileMode.Exists)
            {
                bool exists = path.EndsWith('/') || path.EndsWith('\\')
                    ? Directory.Exists(path)
                    : File.Exists(path);
                token.ThrowIfCancellationRequested();
                return Value.Logical(exists);
            }

            if (entries.Count >= 64 || nextId <= 0)
            {
                return Value.Numeric(-1);
            }

            if (newOutputOnly && (mode & (int)ScriptFileMode.WriteMask) != 0
                && (mode != (int)ScriptFileMode.WriteUtf8 || File.Exists(path)))
            {
                return Value.Numeric(-1);
            }

            FileStream? lease = null;
            try
            {
                string fullPath = path;
                if (Directory.Exists(fullPath)
                    || !Directory.Exists(Path.GetDirectoryName(fullPath)))
                {
                    return Value.Numeric(-1);
                }

                List<string> lines;
                Encoding? automaticEncoding = null;
                bool readable = (mode & (int)ScriptFileMode.Read) != 0;
                bool writable = (mode & (int)ScriptFileMode.WriteMask) != 0;
                bool exclusive = (mode & (int)ScriptFileMode.Exclusive) != 0;
                if (exclusive || readable)
                {
                    try
                    {
                        lease = new FileStream(fullPath,
                            exclusive && writable ? FileMode.OpenOrCreate : FileMode.Open,
                            readable ? writable ? FileAccess.ReadWrite : FileAccess.Read
                                : FileAccess.Write,
                            exclusive ? FileShare.None : FileShare.ReadWrite,
                            4096, FileOptions.Asynchronous);
                    }
                    catch (FileNotFoundException) when (writable && !exclusive)
                    {
                        // 非排他の新規出力はcloseまで物理作成を遅らせます。
                    }

                    lines = [];
                    if (readable && lease is not null)
                    {
                        (lines, automaticEncoding) = await ScriptFileText.ReadOpenLinesAsync(lease, token)
                            .ConfigureAwait(false);
                    }

                    if (!exclusive && lease is not null)
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                        lease = null;
                    }
                }
                else
                {
                    if ((mode & (int)ScriptFileMode.WriteMask) != 0 && File.Exists(fullPath))
                    {
                        // 既存内容を消さずに書込み権限と共有状態を確認します。
                        using var probe = new FileStream(fullPath,
                            FileMode.Open, FileAccess.Write,
                            FileShare.ReadWrite);
                    }

                    lines = [];
                }

                token.ThrowIfCancellationRequested();
                int id = nextId;
                entries.Add(id, new Entry(fullPath, mode, lines, lease,
                    automaticEncoding));
                nextId = id == int.MaxValue ? 0 : id + 1;
                lease = null;
                return Value.Numeric(id);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
            {
                return Value.Numeric(-1);
            }
            finally
            {
                // 読込・復号失敗時にも取得済みの排他ハンドルを解放します。
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private bool Close(int id, bool suppress)
        {
            if (!entries.Remove(id, out Entry? entry))
            {
                if (suppress)
                {
                    return false;
                }

                throw new InvalidOperationException("ファイルIDが無効です。");
            }

            try
            {
                try
                {
                    if ((entry.Mode & (int)ScriptFileMode.WriteMask) == 0)
                    {
                        return true;
                    }

                    string text = ScriptFileText.JoinLines(entry.Lines,
                        (entry.Mode & (int)ScriptFileMode.NoCr) == 0);
                    Encoding encoding = ScriptFileText.WriteEncoding(entry.Mode, text,
                        entry.AutomaticEncoding);
                    // 符号化失敗で既存ファイルを消さないよう先に変換します。
                    byte[] body = encoding.GetBytes(text);
                    byte[] preamble = encoding.GetPreamble();
                    FileStream? output = null;
                    try
                    {
                        FileStream stream = entry.Lease
                            ?? (output = new FileStream(entry.Path,
                                newOutputOnly ? FileMode.CreateNew : FileMode.Create,
                                FileAccess.Write,
                                FileShare.Read));
                        stream.Position = 0;
                        stream.SetLength(0);
                        stream.Write(preamble);
                        stream.Write(body);
                        stream.Flush();
                    }
                    finally
                    {
                        output?.Dispose();
                    }
                }
                finally
                {
                    // 保存の成否にかかわらず排他を解除します。
                    entry.Lease?.Dispose();
                }

                return true;
            }
            catch (Exception ex) when (suppress && (ex is IOException
                or UnauthorizedAccessException or ArgumentException))
            {
                return false;
            }
        }

        private static Value Get(Entry entry, int row,
            int column, int quoteMode, CancellationToken token)
        {
            if (row == -1)
            {
                return Value.Numeric(entry.Lines.Count);
            }

            if (row == -2)
            {
                var text = new StringBuilder();
                for (int i = 0; i < entry.Lines.Count; i++)
                {
                    if ((i & 255) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    text.Append(entry.Lines[i]);
                    text.Append("\r\n");
                }

                token.ThrowIfCancellationRequested();
                return Value.String(text.ToString());
            }

            if (row > entry.Lines.Count)
            {
                return Value.String("");
            }

            string value = entry.Lines[row - 1];
            if (column > 0)
            {
                value = ReadField(value, column,
                    (entry.Mode & (int)ScriptFileMode.Tab) != 0
                        ? '\t' : ',', token);
                value = ScriptFileText.FormatField(value);
            }

            bool suppressNumber = false;
            if (quoteMode == 0 && value.StartsWith('"')
                && value.Length >= 2
                && value.IndexOf('"', 1) == value.Length - 1)
            {
                value = value[1..^1];
                suppressNumber = true;
            }
            else if (quoteMode == 2 && value.StartsWith('"'))
            {
                if (value.Length >= 2 && value.EndsWith('"'))
                {
                    value = value[1..^1];
                }

                value = value.Replace("\"\"", "\"");
            }

            return !suppressNumber
                && NumericOperations.TryFiniteText(value, out double number)
                ? Value.Numeric(number) : Value.String(value);
        }

        private static string ReadField(string line, int column,
            char separator, CancellationToken token)
        {
            bool quoted = false;
            int start = 0;
            int current = 1;
            for (int i = 0; i < line.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (line[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (line[i] == separator && !quoted)
                {
                    if (current == column)
                    {
                        return line[start..i];
                    }

                    current++;
                    start = i + 1;
                }
            }

            token.ThrowIfCancellationRequested();
            return current == column ? line[start..] : "";
        }

        private static void Put(Entry entry, string value,
            int row, int column, int quoteMode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            List<string> lines;
            if (row == -2)
            {
                lines = ScriptFileText.SplitLines(value, token);
            }
            else
            {
                if (column == -1 && (long)row > (long)entry.Lines.Count + 1)
                {
                    throw new InvalidOperationException("挿入行が範囲外です。");
                }

                int count = row == 0 || column == -1
                    ? checked(entry.Lines.Count + 1)
                    : Math.Max(row, entry.Lines.Count);
                lines = new List<string>(count);
                for (int i = 0; i < entry.Lines.Count; i++)
                {
                    if ((i & 255) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    lines.Add(entry.Lines[i]);
                }

                if (row == 0)
                {
                    lines.Add(column > 0
                        ? UpdateColumn("", value, column, quoteMode,
                            (entry.Mode & (int)ScriptFileMode.Tab) != 0
                                ? '\t' : ',', token)
                        : value);
                }
                else if (column == -1)
                {
                    lines.Insert(row - 1, value);
                }
                else
                {
                    while (lines.Count < row)
                    {
                        if ((lines.Count & 255) == 0)
                        {
                            token.ThrowIfCancellationRequested();
                        }

                        lines.Add("");
                    }

                    lines[row - 1] = column > 0
                        ? UpdateColumn(lines[row - 1], value, column,
                            quoteMode, (entry.Mode & (int)ScriptFileMode.Tab) != 0
                                ? '\t' : ',', token)
                        : value;
                }
            }

            token.ThrowIfCancellationRequested();
            entry.Lines = lines;
        }

        private static string UpdateColumn(string line, string value,
            int column, int quoteMode, char separator,
            CancellationToken token)
        {
            int current = 1;
            int start = 0;
            int suffixStart = line.Length;
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (quoteMode != 3 && line[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (line[i] == separator && !quoted)
                {
                    if (current == column)
                    {
                        suffixStart = i + 1;
                        break;
                    }

                    current++;
                    start = i + 1;
                }
            }

            var result = new StringBuilder();
            result.Append(current < column ? line : line.AsSpan(0, start));
            for (int i = current; i < column; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                result.Append(separator);
            }

            // 原版のmode2は区切り文字だけでは引用符を付けません。
            result.Append(quoteMode == 2 && value.Contains('"')
                ? "\"" + value.Replace("\"", "\"\"") + "\"" : value);
            ReadOnlySpan<char> suffix = line.AsSpan(suffixStart);
            for (int i = 0; i < suffix.Length; i++)
            {
                if ((i & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                // 空白だけの残余は落とし、タブと内部の生文字列は保持します。
                if (suffix[i] > ' ' || suffix[i] == '\t')
                {
                    result.Append(separator);
                    result.Append(suffix);
                    break;
                }
            }

            token.ThrowIfCancellationRequested();
            return result.ToString();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            List<Exception> errors = [];
            foreach (int id in entries.Keys.ToArray())
            {
                try
                {
                    Close(id, false);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            return errors.Count == 0 ? ValueTask.CompletedTask
                : ValueTask.FromException(new AggregateException(errors));
        }
    }

}
