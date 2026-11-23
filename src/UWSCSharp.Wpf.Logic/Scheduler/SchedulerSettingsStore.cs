using System.IO;

namespace UWSCSharp.Wpf;

internal sealed class SchedulerSettingsConflictException : IOException
{
    internal SchedulerSettingsConflictException()
        : base("別の保存で予約設定が更新されています。再読込みしてください。")
    {
    }
}

/// <summary>
/// 短い排他と保存識別子の照合で、旧画面からの上書きを防ぎます。
/// </summary>
internal static class SchedulerSettingsStore
{
    internal static SchedulerSettingsSnapshot Load(string path) =>
        LoadWithInitialCapacity(path, null);

    internal static SchedulerSettingsSnapshot LoadWithInitialCapacity(string path,
        Func<int>? initialCapacity)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return new SchedulerSettingsSnapshot(new SchedulerSettings(
                capacity: initialCapacity?.Invoke() ?? SchedulerSettings.InitialCapacity), null);
        }
        catch (DirectoryNotFoundException)
        {
            return new SchedulerSettingsSnapshot(new SchedulerSettings(
                capacity: initialCapacity?.Invoke() ?? SchedulerSettings.InitialCapacity), null);
        }

        using (stream)
        {
            if (stream.Length > SchedulerValidation.MaximumBytes)
            {
                throw new InvalidDataException("予約設定の読込み上限は4MiBです。");
            }

            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException("読込み中に予約設定のサイズが変わりました。");
            }

            return SchedulerSettingsJson.Read(bytes);
        }
    }

    internal static SchedulerSettingsSnapshot Save(string path,
        SchedulerSettings settings, Guid? expectedRevision,
        Action<string, string?>? beforeCommit = null)
    {
        string destination = Path.GetFullPath(path);
        var candidate = new SchedulerSettingsSnapshot(settings, Guid.NewGuid());
        byte[] bytes = SchedulerSettingsJson.Write(settings, candidate.Revision!.Value);
        using FileStream ownership = Acquire(destination);
        SchedulerSettingsSnapshot current = Load(destination);
        if (current.Revision != expectedRevision)
        {
            throw new SchedulerSettingsConflictException();
        }

        string? backup = current.FormatVersion == 1
            ? destination + "." + Guid.NewGuid().ToString("N") + ".version1.json" : null;
        Commit(destination, bytes, expectedRevision.HasValue, backup, beforeCommit);
        return candidate with
        {
            PreviousFormatBackupPath = backup
        };
    }

    internal static (SchedulerSettingsSnapshot Snapshot, string? BackupPath) ResetWithBackup(
        string path, Action<string, string?>? beforeCommit = null)
    {
        string destination = Path.GetFullPath(path);
        var candidate = new SchedulerSettingsSnapshot(new SchedulerSettings(), Guid.NewGuid());
        byte[] bytes = SchedulerSettingsJson.Write(candidate.Settings, candidate.Revision!.Value);
        using FileStream ownership = Acquire(destination);
        string? backup = File.Exists(destination)
            ? destination + "." + Guid.NewGuid().ToString("N") + ".rejected.json" : null;
        Commit(destination, bytes, backup is not null, backup, beforeCommit);
        return (candidate, backup);
    }

    private static FileStream Acquire(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // 排他ファイルは消さず、ハンドルを閉じた後の再作成競合を避ける。
        return new FileStream(destination + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private static void Commit(string destination, byte[] bytes, bool replaceExisting, string? backup,
        Action<string, string?>? beforeCommit)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        AtomicFileWriter.Write(temporary, destination,
            file => file.Write(bytes),
            replaceExisting ? AtomicFileWriter.CommitMode.Replace
                : AtomicFileWriter.CommitMode.Move,
            "予約設定の一時ファイル削除エラー", backup, beforeCommit);
    }
}
