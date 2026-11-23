using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 既定ログと履歴の管理領域を、親とworkerで共通の排他により保存します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SchedulerManagedStorage : IScriptLogStoragePolicy
{
    private static readonly TimeSpan StorageLockTimeout = TimeSpan.FromSeconds(5);
    private const int StorageLockRetryMilliseconds = 25;

    internal const long MaximumLogBytes = 64L * 1024 * 1024;
    private readonly string root;
    private readonly string managed;
    private readonly string logs;
    private readonly string histories;
    private readonly long maximumLogBytes;
    private readonly SchedulerHistoryStore history;

    internal SchedulerManagedStorage(string root,
        long maximumLogBytes = MaximumLogBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLogBytes);
        this.root = Path.GetFullPath(root);
        managed = Path.Combine(this.root, "managed");
        logs = Path.Combine(managed, "logs");
        histories = Path.Combine(managed, "history");
        this.maximumLogBytes = maximumLogBytes;
        history = new SchedulerHistoryStore(histories);
    }

    internal string LogPath(Guid occurrenceId) =>
        Path.Combine(logs, occurrenceId.ToString("N") + ".log");

    internal IReadOnlyList<SchedulerHistoryRecord> LoadHistory() => history.Load();

    internal SchedulerHistoryPage ReadHistoryPage(SchedulerHistoryCursor? before,
        int pageSize, CancellationToken cancellationToken)
    {
        CheckHistoryReadLayout();
        return history.ReadPage(before, pageSize, cancellationToken);
    }

    internal SchedulerHistoryRecord ReadHistoryRecord(Guid id)
    {
        CheckHistoryReadLayout();
        return history.ReadRecord(id);
    }

    private void CheckHistoryReadLayout()
    {
        foreach (string directory in new[] { root, managed, histories })
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("履歴の保存先がフォルダーではありません。");
            }
            CheckNoLink(directory);
        }
    }

    public async ValueTask ValidateExplicitPathAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string resolvedRoot = WindowsStoragePath.Resolve(root);
        string candidate = WindowsStoragePath.Resolve(path);
        if (Contains(resolvedRoot, candidate))
        {
            throw new IOException("予約の管理領域を明示ログに指定できません。空のパスで既定へ戻してください。");
        }
        if (File.Exists(candidate) && Directory.Exists(root))
        {
            using IDisposable ownership = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            (ulong volume, Guid id) = WindowsStoragePath.Identity(candidate);
            foreach (string owned in EnumerateLogFiles(includeHistory: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(owned, Path.Combine(managed, ".storage.lock"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(owned, Path.Combine(histories, ".history.lock"), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (WindowsStoragePath.Identity(owned) == (volume, id))
                {
                    throw new IOException("予約の管理ファイルの別名を明示ログに指定できません。");
                }
            }
        }
    }

    public async ValueTask<IDisposable> AcquireDefaultSaveAsync(string path,
        long prospectiveBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prospectiveBytes);
        IDisposable ownership = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string resolved = WindowsStoragePath.Resolve(path);
            if (!Contains(WindowsStoragePath.Resolve(logs), resolved)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id)
                || !string.Equals(Path.GetFileName(path), id.ToString("N") + ".log", StringComparison.Ordinal))
            {
                throw new IOException("予約の既定ログの保存先が管理領域と一致しません。");
            }
            long usage = MeasureLogBytes();
            if (prospectiveBytes > maximumLogBytes || usage > maximumLogBytes)
            {
                throw new ScriptLogStorageQuotaException("予約ログの要求または既存容量が上限を超えるため、既存ファイルを保持して保存を省略しました。");
            }
            if (usage > maximumLogBytes - prospectiveBytes)
            {
                foreach (SchedulerHistoryRecord record in history.Load()
                    .Where(record => !record.IsProtected)
                    .OrderBy(record => record.EndedAt).ThenBy(record => record.OccurrenceId))
                {
                    if (record.OccurrenceId == id)
                    {
                        continue;
                    }
                    DeleteLog(record);
                    usage = MeasureLogBytes();
                    if (usage <= maximumLogBytes - prospectiveBytes)
                    {
                        break;
                    }
                }
            }
            if (usage > maximumLogBytes - prospectiveBytes)
            {
                throw new ScriptLogStorageQuotaException("予約ログ全体の容量を確保できないため、今回の既定ログ保存を省略しました。");
            }
            return ownership;
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    internal async Task SaveHistoryAsync(SchedulerHistoryRecord record,
        CancellationToken cancellationToken = default)
    {
        using IDisposable ownership = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => history.Save(record, DeleteLog), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        foreach (string directory in new[] { root, managed, logs, histories })
        {
            if (Directory.Exists(directory))
            {
                CheckNoLink(directory);
            }
            Directory.CreateDirectory(directory);
            CheckNoLink(directory);
        }
        string path = Path.Combine(managed, ".storage.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                CheckNoLink(path);
            }
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                && elapsed.Elapsed < StorageLockTimeout)
            {
                await Task.Delay(StorageLockRetryMilliseconds, token).ConfigureAwait(false);
            }
        }
    }

    private long MeasureLogBytes()
    {
        long total = 0;
        foreach (string path in EnumerateLogFiles())
        {
            total = checked(total + new FileInfo(path).Length);
        }
        return total;
    }

    private IEnumerable<string> EnumerateLogFiles(bool includeHistory = false)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            CheckNoLink(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                CheckNoLink(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (includeHistory || !string.Equals(path, histories, StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(path);
                    }
                }
                else
                {
                    yield return path;
                }
            }
        }
    }

    private void DeleteLog(SchedulerHistoryRecord record)
    {
        string path = LogPath(record.OccurrenceId);
        if (Directory.Exists(path))
        {
            throw new IOException("予約ログの保存先がフォルダーに置き換わっています。");
        }
        if (File.Exists(path))
        {
            CheckNoLink(path);
            File.Delete(path);
        }
    }

    private static bool Contains(string directory, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(directory)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CheckNoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("リンクされた予約管理領域は自動更新しません。");
        }
    }
}
