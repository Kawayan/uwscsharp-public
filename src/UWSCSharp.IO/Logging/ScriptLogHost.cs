using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// 永続ログへ付ける時刻の形式です。
/// </summary>
public enum ScriptLogMode
{
    /// <summary>
    /// 分単位の日時を付けます。
    /// </summary>
    Minutes = 0,
    /// <summary>
    /// 表示だけを行い、保存しません。
    /// </summary>
    Disabled = 1,
    /// <summary>
    /// 日時を付けずに本文だけを保存します。
    /// </summary>
    TextOnly = 2,
    /// <summary>
    /// 秒単位の日時を付けます。
    /// </summary>
    Seconds = 3
}

/// <summary>
/// 表示先を維持し、出力を上限付きの永続ログへ保存します。
/// </summary>
public sealed partial class ScriptLogHost : IScriptLogHost, IScriptLogViewHost,
    IScriptStopFormOptionsHost, IScriptWindowPositionHost
{
    private const int MaximumTextLength = 64_000;
    private static readonly SemaphoreSlim[] FileGates =
        [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly IScriptHost inner;
    private readonly string defaultPath;
    private string path;
    private int maximumLines;
    private ScriptLogMode mode;
    private readonly TimeProvider clock;
    private readonly bool replaceInvalidText;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LogEntries pending = new();
    private readonly int? maximumDefaultBytes;
    private readonly IScriptLogStoragePolicy? storagePolicy;
    private bool defaultLogSelected = true;
    private long omittedDefaultBytes;
    private bool clearExisting;
    private LogEntries? savedEntries;
    private byte[]? savedHash;
    private string? savedPath;

    /// <summary>
    /// 保存先・上限・時刻形式を指定します。相対パスは受け付けません。
    /// 構築だけではファイルを読み書きせず、上限0では保存しません。
    /// </summary>
    public ScriptLogHost(IScriptHost inner, string path,
        int maximumLines = 2000, ScriptLogMode mode = ScriptLogMode.Minutes,
        bool clearExisting = false, TimeProvider? timeProvider = null)
        : this(inner, path, false, maximumLines, mode, clearExisting, timeProvider)
    {
    }

    /// <summary>
    /// 保存本文の不正UTF-16を置換するか指定してログhostを作成します。
    /// </summary>
    public ScriptLogHost(IScriptHost inner, string path, bool replaceInvalidText,
        int maximumLines = 2000, ScriptLogMode mode = ScriptLogMode.Minutes,
        bool clearExisting = false, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("ログには絶対パスを指定してください。", nameof(path));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumLines);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLines, 100_000);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        this.inner = inner;
        this.path = Path.GetFullPath(path);
        defaultPath = this.path;
        this.maximumLines = maximumLines;
        this.mode = mode;
        this.clearExisting = clearExisting;
        clock = timeProvider ?? TimeProvider.System;
        this.replaceInvalidText = replaceInvalidText;
    }

    /// <summary>
    /// 既定ログにバイト上限を適用します。明示した保存先には適用しません。
    /// 空のパスで既定へ戻した場合は再び適用します。
    /// </summary>
    public ScriptLogHost(IScriptHost inner, string path, ScriptLogByteLimit byteLimit,
        bool replaceInvalidText = false, int maximumLines = 2000,
        ScriptLogMode mode = ScriptLogMode.Minutes, bool clearExisting = false,
        TimeProvider? timeProvider = null)
        : this(inner, path, replaceInvalidText, maximumLines, mode, clearExisting, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(byteLimit);
        maximumDefaultBytes = byteLimit.MaximumBytes;
    }

    /// <summary>
    /// 既定ログの容量と明示パスを指定の管理方針で検査します。
    /// </summary>
    public ScriptLogHost(IScriptHost inner, string path, ScriptLogByteLimit byteLimit,
        IScriptLogStoragePolicy storagePolicy, bool replaceInvalidText = false,
        int maximumLines = 2000, ScriptLogMode mode = ScriptLogMode.Minutes,
        bool clearExisting = false, TimeProvider? timeProvider = null)
        : this(inner, path, byteLimit, replaceInvalidText, maximumLines,
            mode, clearExisting, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(storagePolicy);
        this.storagePolicy = storagePolicy;
    }

    /// <summary>
    /// 既定ログのバイト上限で省略したUTF-8の累計バイト数を返します。
    /// </summary>
    public long OmittedDefaultBytes => Interlocked.Read(ref omittedDefaultBytes);

    /// <summary>
    /// 設定切替時に省略した既定ログの容量診断を返します。
    /// </summary>
    public ScriptLogStorageQuotaException? LastStorageOmission
    {
        get; private set;
    }

    private int? SelectedByteLimit => defaultLogSelected ? maximumDefaultBytes : null;

    /// <summary>
    /// 表示先への出力が成功した後、ログ用の本文を保持します。
    /// </summary>
    public async ValueTask WriteOutputAsync(string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        await inner.WriteOutputAsync(text, cancellationToken).ConfigureAwait(false);
        // 表示済みの出力は、直後に取消されても保存対象へ含めます。
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mode == ScriptLogMode.Disabled || maximumLines == 0)
            {
                return;
            }

            string entry = Limit(text, MaximumTextLength);
            if (replaceInvalidText)
            {
                entry = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(entry));
            }

            if (mode != ScriptLogMode.TextOnly)
            {
                string format = mode == ScriptLogMode.Seconds
                    ? "yy/MM/dd HH:mm:ss  " : "yy/MM/dd HH:mm  ";
                entry = clock.GetLocalNow().ToString(format,
                    CultureInfo.InvariantCulture) + entry;
            }

            long previous = pending.OmittedBytes;
            Add(pending, entry);
            Interlocked.Add(ref omittedDefaultBytes, pending.OmittedBytes - previous);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 待機は表示先のhostへ委譲します。
    /// </summary>
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        inner.DelayAsync(duration, cancellationToken);

    /// <summary>
    /// 表示先のログウィンドウ対応状況を返します。
    /// </summary>
    public bool SupportsLogView => inner is IScriptLogViewHost view && view.SupportsLogView;

    /// <summary>
    /// 実際の表示先の停止画面への対応状況を返します。
    /// </summary>
    public bool SupportsStopForm => inner is IScriptStopFormHost target && target.SupportsStopForm;

    /// <summary>
    /// 表示先の画面配置への対応状況を返します。
    /// </summary>
    public bool SupportsWindowPosition => inner is IScriptWindowPositionHost target
        && target.SupportsWindowPosition;

    /// <summary>
    /// 停止画面とログ画面への配置要求を表示先へ転送します。
    /// </summary>
    public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
        CancellationToken cancellationToken) =>
        inner is IScriptWindowPositionHost target && target.SupportsWindowPosition
            ? target.SetWindowPositionAsync(request, cancellationToken)
            : ValueTask.FromException(new NotSupportedException(
                "表示先は画面の配置変更に対応していません。"));

    /// <summary>
    /// 実際のホストの停止設定への対応状況を返します。
    /// </summary>
    public bool SupportsStopFormOptions => inner is IScriptStopFormOptionsHost target
        && target.SupportsStopFormOptions;

    /// <summary>
    /// 設定の保持と復元を実際の実行元へ転送します。
    /// </summary>
    public ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
        CancellationToken cancellationToken) =>
        inner is IScriptStopFormOptionsHost target && target.SupportsStopFormOptions
            ? target.CreateStopFormOptionsSessionAsync(cancellationToken)
            : ValueTask.FromException<IScriptStopFormOptionsSession>(new NotSupportedException(
                "表示先は停止設定に対応していません。"));

    /// <summary>
    /// 停止画面の要求をログ保存と分けて実行元へ渡します。
    /// </summary>
    public ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken cancellationToken) =>
        inner is IScriptStopFormHost target && target.SupportsStopForm
            ? target.SetStopFormAsync(request, cancellationToken)
            : ValueTask.FromException(new NotSupportedException(
                "表示先は停止画面に対応していません。"));

    /// <summary>
    /// 表示操作を永続ログと分けて表示先へ渡します。
    /// </summary>
    public ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken cancellationToken) =>
        inner is IScriptLogViewHost target && target.SupportsLogView
            ? target.SetLogViewAsync(view, cancellationToken)
            : ValueTask.FromException(new NotSupportedException(
                "表示先はログウィンドウに対応していません。"));

    /// <summary>
    /// 対応する表示先へ履歴の保持件数を渡します。
    /// </summary>
    public ValueTask SetLogHistoryLimitAsync(int maximumLines,
        CancellationToken cancellationToken) =>
        inner is IScriptLogViewHost target && target.SupportsLogView
            ? target.SetLogHistoryLimitAsync(maximumLines, cancellationToken)
            : ValueTask.CompletedTask;

    /// <summary>
    /// 旧設定の出力を保存してから、新しいログ設定を適用します。
    /// </summary>
    public async ValueTask ConfigureLogAsync(ScriptLogSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumLines is int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximum);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 100_000);
        }

        if (settings.FileMode is int fileMode)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(fileMode);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(fileMode, 7);
        }

        string? selectedPath = settings.Path;
        if (selectedPath is not null)
        {
            if (selectedPath.Any(character => character < ' '))
            {
                throw new ArgumentException("ログのパスに制御文字は使用できません。");
            }

            selectedPath = selectedPath.Length == 0 ? defaultPath
                : Path.GetFullPath(selectedPath, Path.GetDirectoryName(defaultPath)!);
            if (Path.EndsInDirectorySeparator(selectedPath) || Directory.Exists(selectedPath))
            {
                selectedPath = Path.Combine(selectedPath, "UWSC.LOG");
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (storagePolicy is not null && settings.Path is { Length: > 0 })
            {
                try
                {
                    await storagePolicy.ValidateExplicitPathAsync(selectedPath!,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ArgumentException("ログの保存先を指定できません: " + error.Message,
                        nameof(settings), error);
                }
            }
            try
            {
                await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ScriptLogStorageQuotaException error)
            {
                LastStorageOmission = error;
                Interlocked.Add(ref omittedDefaultBytes,
                    pending.Sum(line => Utf8.GetByteCount(line) + 2L));
                pending.Clear();
            }
            path = selectedPath ?? path;
            if (settings.Path is not null)
            {
                defaultLogSelected = settings.Path.Length == 0;
            }
            maximumLines = settings.MaximumLines ?? maximumLines;
            if (settings.FileMode is int selectedMode)
            {
                mode = (ScriptLogMode)(selectedMode & 3);
                clearExisting = (selectedMode & 4) != 0;
            }
        }
        finally
        {
            gate.Release();
        }

        if (settings.MaximumLines is int historyLimit)
        {
            // 表示先への呼出しでログ側のgateを保持しません。
            await SetLogHistoryLimitAsync(historyLimit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 既存の末尾と未保存データを同じディレクトリの一時ファイルへ書き、置換します。
    /// 失敗時は未保存データを保持し、呼出し元が再試行できます。
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        uint hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        SemaphoreSlim fileGate = FileGates[hash % (uint)FileGates.Length];
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lines = new LogEntries();
            if (!clearExisting)
            {
                byte[]? existingHash = await ReadExistingAsync(lines,
                    cancellationToken).ConfigureAwait(false);
                if (savedEntries is not null && savedHash is not null
                    && existingHash is not null
                    && string.Equals(savedPath, path, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && existingHash.AsSpan().SequenceEqual(savedHash))
                {
                    // 自分の保存内容が不変なら、改行を含む出力単位を維持します。
                    lines.Clear();
                    foreach (string entry in savedEntries)
                    {
                        Add(lines, entry);
                    }
                }
            }

            foreach (string line in pending)
            {
                Add(lines, line);
            }

            byte[] fingerprint = await SaveAsync(lines,
                cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref omittedDefaultBytes, lines.OmittedBytes);
            savedEntries = lines;
            savedHash = fingerprint;
            savedPath = path;
            pending.Clear();
            clearExisting = false;
        }
        finally
        {
            fileGate.Release();
        }
    }

    /// <summary>
    /// 主スクリプトのフォルダーのUWSC.LOGを既定にして実行します。
    /// 保存本文の不正UTF-16は表示先と同様に置換文字へ変換します。
    /// </summary>
    public static Task<int> RunScriptAsync(IScriptHost output, string scriptPath,
        Func<IScriptHost, Task<int>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        string logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!,
            "UWSC.LOG");
        return RunAsync(output, logPath, run, replaceInvalidText: true);
    }

    /// <summary>
    /// 取消時にも表示する追加の後処理診断があるか返します。
    /// </summary>
    public static bool HasAdditionalDiagnostics(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data["UWSCSharp.FinallyErrors"] is AggregateException
            || error.Data["UWSCSharp.CleanupErrors"] is AggregateException;
    }

    /// <summary>
    /// 実行時の診断に、FINALLYと終了時の保存・解放の失敗を付記します。
    /// </summary>
    public static string FormatDiagnostic(Exception error, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        message ??= DiagnosticMessage(error);
        List<string> diagnostics = [message];
        if (error.Data["UWSCSharp.FinallyErrors"] is AggregateException finallyErrors)
        {
            foreach (Exception failure in finallyErrors.InnerExceptions)
            {
                diagnostics.Add("FINALLY: " + DiagnosticMessage(failure));
            }
        }

        if (error.Data["UWSCSharp.CleanupErrors"] is AggregateException cleanup)
        {
            diagnostics.Add("終了処理: " + cleanup.Message);
        }

        return string.Join(Environment.NewLine, diagnostics);
    }

    private static string DiagnosticMessage(Exception error) =>
        error is ScriptException scriptError
            ? scriptError.DiagnosticMessage : error.Message;

    /// <summary>
    /// 実行終了・失敗・取消の後にログ保存を試みます。
    /// 保存も失敗した場合は元の例外を保持し、解放エラー情報へ保存失敗を追加します。
    /// </summary>
    public static Task<int> RunAsync(IScriptHost output, string path,
        Func<IScriptHost, Task<int>> run, int maximumLines = 2000,
        ScriptLogMode mode = ScriptLogMode.Minutes, bool clearExisting = false,
        TimeProvider? timeProvider = null) => RunAsync(output, path, run, false,
            maximumLines, mode, clearExisting, timeProvider);

    /// <summary>
    /// 保存本文の不正UTF-16の扱いを指定し、終了時にログを保存します。
    /// </summary>
    public static async Task<int> RunAsync(IScriptHost output, string path,
        Func<IScriptHost, Task<int>> run, bool replaceInvalidText,
        int maximumLines = 2000, ScriptLogMode mode = ScriptLogMode.Minutes,
        bool clearExisting = false, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var host = new ScriptLogHost(output, path, replaceInvalidText,
            maximumLines, mode, clearExisting, timeProvider);
        return await RunWithHostAsync(host, run).ConfigureAwait(false);
    }

    private void Add(LogEntries lines, string text) =>
        lines.Add(text, maximumLines, SelectedByteLimit);

    private async Task<byte[]?> ReadExistingAsync(
        LogEntries lines, CancellationToken token)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        using var hash = SHA256.Create();
        using var hashed = new CryptoStream(stream, hash, CryptoStreamMode.Read);
        using var reader = new StreamReader(hashed, Utf8, true);
        char[] buffer = new char[4096];
        var line = new StringBuilder();
        bool afterCr = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)
            .ConfigureAwait(false)) > 0)
        {
            for (int index = 0; index < count; index++)
            {
                char character = buffer[index];
                if (character == '\n' && afterCr)
                {
                    afterCr = false;
                    continue;
                }

                afterCr = character == '\r';
                if (character is '\r' or '\n')
                {
                    AddLine();
                }
                else if (line.Length < MaximumTextLength + 33)
                {
                    line.Append(character);
                }
            }
        }

        if (line.Length > 0)
        {
            AddLine();
        }

        return hash.Hash!;

        void AddLine()
        {
            Add(lines, Limit(line.ToString(), MaximumTextLength + 32));
            line.Clear();
        }
    }

    private async Task<byte[]> SaveAsync(LogEntries lines,
        CancellationToken token)
    {
        using IDisposable? storageLease = defaultLogSelected && storagePolicy is not null
            ? await storagePolicy.AcquireDefaultSaveAsync(path,
                lines.Sum(line => Utf8.GetByteCount(line) + 2L), token).ConfigureAwait(false)
            : null;
        string temporary = Path.Combine(Path.GetDirectoryName(path)!,
            ".uwsc-log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var hash = SHA256.Create();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await using (var hashed = new CryptoStream(stream, hash,
                CryptoStreamMode.Write))
            {
                await using (var writer = new StreamWriter(hashed, Utf8,
                    leaveOpen: true))
                {
                    writer.NewLine = "\r\n";
                    foreach (string line in lines)
                    {
                        await writer.WriteLineAsync(line.AsMemory(), token)
                            .ConfigureAwait(false);
                    }

                    await writer.FlushAsync(token).ConfigureAwait(false);
                }

                await hashed.FlushFinalBlockAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return hash.Hash!;
        }
        catch (Exception failure)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                failure.Data["UWSCSharp.CleanupErrors"] = new AggregateException(cleanup);
            }

            throw;
        }
    }

    private static string Limit(string text, int maximum)
    {
        if (text.Length <= maximum)
        {
            return text;
        }

        if (maximum > 0 && char.IsHighSurrogate(text[maximum - 1])
            && char.IsLowSurrogate(text[maximum]))
        {
            maximum--;
        }

        return text[..maximum] + "...";
    }
}
