using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UWSCSharp.Wpf;

/// <summary>
/// 履歴を発生識別子ごとに置換し、回収済みの古い履歴だけを整理します。
/// </summary>
internal sealed partial class SchedulerHistoryStore
{
    internal const int MaximumCompletedRecords = 100;
    internal const int MaximumRecordBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly string directory;

    internal SchedulerHistoryStore(string directory) =>
        this.directory = System.IO.Path.GetFullPath(directory);

    internal IReadOnlyList<SchedulerHistoryRecord> Load()
    {
        if (!HasHistoryDirectory())
        {
            return [];
        }
        CheckDirectory();
        var records = new List<SchedulerHistoryRecord>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                SchedulerHistoryRecord record = Read(path);
                if (record.OccurrenceId != id
                    || System.IO.Path.GetFileNameWithoutExtension(path) != id.ToString("N"))
                {
                    throw new InvalidDataException("履歴のファイル名と発生識別子が一致しません。");
                }
                records.Add(record);
            }
        }
        return records;
    }

    internal void Save(SchedulerHistoryRecord record,
        Action<SchedulerHistoryRecord>? beforeDelete = null)
    {
        Validate(record);
        record = record with
        {
            Version = SchedulerHistoryRecord.CurrentVersion
        };
        Directory.CreateDirectory(directory);
        CheckDirectory();
        // 排他ファイルは残し、ハンドルの閉鎖と再作成の競合を避けます。
        using var ownership = new FileStream(System.IO.Path.Combine(directory, ".history.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string destination = System.IO.Path.Combine(directory, record.OccurrenceId.ToString("N") + ".json");
        bool replace = true;
        if (File.Exists(destination))
        {
            SchedulerHistoryRecord previous = Read(destination);
            CheckIdentity(previous, record);
            if (record.Revision < previous.Revision)
            {
                return;
            }
            if (record.Revision == previous.Revision)
            {
                // 同じ更新版は値で比較し、書込み不要の旧履歴を再構成しません。
                SchedulerHistoryRecord comparable = previous with
                {
                    Version = record.Version,
                    Arguments = record.Arguments
                };
                if (comparable != record)
                {
                    throw new InvalidDataException("同じ履歴版に異なる内容を保存できません。");
                }
                replace = false;
            }
        }
        if (replace)
        {
            using var bytes = new LimitedMemoryStream(MaximumRecordBytes,
                "予約履歴が保存容量を超えています。");
            JsonSerializer.Serialize(bytes, record, Options);
            string temporary = System.IO.Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            AtomicFileWriter.Write(temporary, destination,
                file => file.Write(bytes.GetBuffer().AsSpan(
                    0, checked((int)bytes.Length))),
                AtomicFileWriter.CommitMode.Overwrite,
                "履歴の一時ファイル削除エラー");
        }
        SchedulerHistoryRecord[] obsolete = [.. Load().Where(value => !value.IsProtected)
            .OrderByDescending(value => value.EndedAt)
            .ThenByDescending(value => value.OccurrenceId)
            .Skip(MaximumCompletedRecords)];
        foreach (SchedulerHistoryRecord value in obsolete)
        {
            beforeDelete?.Invoke(value);
            File.Delete(System.IO.Path.Combine(directory, value.OccurrenceId.ToString("N") + ".json"));
        }
    }

    private void CheckDirectory()
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("リンクされた履歴フォルダーは自動更新しません。");
        }
    }

    private static SchedulerHistoryRecord Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("リンクされた履歴ファイルは読み書きしません。");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaximumRecordBytes)
        {
            throw new InvalidDataException("予約履歴の容量が不正です。");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        SchedulerJsonValidation.Validate(bytes, 8);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("予約履歴にはオブジェクトが必要です。");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException("予約履歴の項目が重複しています。");
            }
        }
        ValidateSchema(document.RootElement, names);
        SchedulerHistoryRecord record = JsonSerializer.Deserialize<SchedulerHistoryRecord>(bytes, Options)
            ?? throw new InvalidDataException("予約履歴が空です。");
        Validate(record);
        return record with
        {
            Arguments = Array.AsReadOnly(record.Arguments.ToArray())
        };
    }

    private static void Validate(SchedulerHistoryRecord record)
    {
        if (record.Version is not (2 or SchedulerHistoryRecord.CurrentVersion)
            || record.Version == 2 && record.Window is not null
            || record.Window is { } window && (window.Handle == 0 || window.ProcessId == 0
                || window.ThreadId == 0 || window.ObservedAt != record.AcceptedAt
                || window.ObservedAt.Offset != TimeSpan.Zero)
            || record.OccurrenceId == Guid.Empty
            || record.Generation == Guid.Empty || record.EntryId == Guid.Empty
            || record.Revision < 0 || !Enum.IsDefined(record.Mode)
            || record.Outcome is { } outcome && !Enum.IsDefined(outcome)
            || record.ResultObservedAt.HasValue != record.Outcome.HasValue
            || record.EndedAt is not null && record.ResultObservedAt is null
            || record.ResourcesRecovered && record.EndedAt is null
            || record.ResultObservedAt is null && (record.ResourcesRecovered || record.Forced || record.ExitCode is not null)
            || record.Arguments is null || record.Arguments.Count > UWSCSharp.Core.UwscArray.MaximumElements
            || record.Path is null || string.IsNullOrWhiteSpace(record.Title)
            || !System.IO.Path.IsPathFullyQualified(record.Path)
            || record.StartedAt is not null && !record.Started
            || record.Outcome == SchedulerExecutionOutcome.Succeeded
                && (record.ExitCode != 0 || !record.ResourcesRecovered)
            || record.Outcome == SchedulerExecutionOutcome.Cancelled && record.ExitCode != 130)
        {
            throw new InvalidDataException("予約履歴の版・識別子・結果が不正です。");
        }
        SchedulerValidation.Text(record.Title);
        SchedulerValidation.Text(record.Path);
        foreach (string argument in record.Arguments)
        {
            SchedulerValidation.Text(argument);
        }
        if (record.Diagnostic is not null)
        {
            SchedulerValidation.Text(record.Diagnostic);
        }
    }

    private static void CheckIdentity(SchedulerHistoryRecord previous, SchedulerHistoryRecord next)
    {
        if (next.Revision >= previous.Revision
            && (previous.StartedAt is not null && next.StartedAt != previous.StartedAt
            || previous.ResultObservedAt is not null && (next.ResultObservedAt != previous.ResultObservedAt
                || previous.EndedAt is not null && next.EndedAt != previous.EndedAt
                || next.Outcome != previous.Outcome || next.ExitCode != previous.ExitCode
                || next.Started != previous.Started || next.Forced != previous.Forced
                || next.Diagnostic != previous.Diagnostic
                || previous.ResourcesRecovered && !next.ResourcesRecovered)))
        {
            throw new InvalidDataException("確認済みの履歴結果を別の結果で上書きできません。");
        }
        if (previous.Generation != next.Generation || previous.EntryId != next.EntryId
            || previous.Window != next.Window
            || previous.Title != next.Title || previous.Path != next.Path
            || previous.Mode != next.Mode || previous.AcceptedAt != next.AcceptedAt
            || !previous.Arguments.SequenceEqual(next.Arguments))
        {
            throw new InvalidDataException("受付時の履歴定義を別の定義で上書きできません。");
        }
    }
}
