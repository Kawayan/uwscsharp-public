using System.IO;
using System.Text.Json;

namespace UWSCSharp.Wpf;

internal sealed partial class SchedulerHistoryStore
{
    private static void ValidateSchema(JsonElement root, HashSet<string> names)
    {
        if (!root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value)
            || value is not (2 or SchedulerHistoryRecord.CurrentVersion)
            || names.Contains("window") != (value == SchedulerHistoryRecord.CurrentVersion))
        {
            throw new InvalidDataException("予約履歴の版または捕捉先の項目が不正です。");
        }

        if (value == 2 || root.GetProperty("window").ValueKind == JsonValueKind.Null)
        {
            return;
        }

        JsonElement window = root.GetProperty("window");
        if (window.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("予約履歴の捕捉先にはオブジェクトが必要です。");
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in window.EnumerateObject())
        {
            if (property.Name is not ("handle" or "processId" or "threadId" or "observedAt")
                || !fields.Add(property.Name))
            {
                throw new InvalidDataException("予約履歴の捕捉先が未知または重複しています。");
            }
        }

        if (fields.Count != 4)
        {
            throw new InvalidDataException("予約履歴の捕捉先の項目が不足しています。");
        }

        JsonElement handle = window.GetProperty("handle");
        JsonElement process = window.GetProperty("processId");
        JsonElement thread = window.GetProperty("threadId");
        JsonElement observed = window.GetProperty("observedAt");
        if (handle.ValueKind != JsonValueKind.Number || !handle.TryGetUInt64(out _)
            || process.ValueKind != JsonValueKind.Number || !process.TryGetUInt32(out _)
            || thread.ValueKind != JsonValueKind.Number || !thread.TryGetUInt32(out _)
            || observed.ValueKind != JsonValueKind.String || !observed.TryGetDateTimeOffset(out _))
        {
            throw new InvalidDataException("予約履歴の捕捉先の型または範囲が不正です。");
        }
    }
}
