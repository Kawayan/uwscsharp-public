using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.Wpf;

internal sealed record SchedulerSettingsSnapshot(SchedulerSettings Settings, Guid? Revision,
    int FormatVersion = 2, string? PreviousFormatBackupPath = null);

/// <summary>
/// 予約設定の保存形式を、実行用のモデルから分離します。
/// </summary>
internal static partial class SchedulerSettingsJson
{
    internal static SchedulerSettingsSnapshot Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SchedulerValidation.MaximumBytes)
        {
            throw new InvalidDataException("予約設定の読込み上限は4MiBです。");
        }

        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            bytes = bytes[3..];
        }

        try
        {
            SchedulerJsonValidation.Validate(bytes, 16);

            using var document = JsonDocument.Parse(bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 16 });
            int version = document.RootElement.GetProperty("version").GetInt32();
            if (version is not (1 or 2))
            {
                throw new InvalidDataException("未対応の予約設定バージョンです。");
            }

            string[] keys = version == 1
                ? ["version", "revision", "capacity", "enabled", "beep", "wakeUp", "slots"]
                : ["version", "revision", "capacity", "enabled", "beep", "wakeUp", "slots", "oneShotStates"];
            Dictionary<string, JsonElement> root = Object(document.RootElement, keys);

            var revision = Guid.Parse(Text(root["revision"]));
            if (revision == Guid.Empty)
            {
                throw new InvalidDataException("予約設定の保存識別子が空です。");
            }

            int capacity = root["capacity"].GetInt32();
            if (capacity is < SchedulerSettings.InitialCapacity or > SchedulerSettings.MaximumCapacity)
            {
                throw new InvalidDataException("予約の容量は40〜400枠で指定してください。");
            }

            JsonElement slots = root["slots"];
            if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() > capacity)
            {
                throw new InvalidDataException("予約設定の枠が不正です。");
            }

            var entries = new List<SchedulerEntry?>();
            foreach (JsonElement slot in slots.EnumerateArray())
            {
                entries.Add(slot.ValueKind == JsonValueKind.Null ? null : Entry(slot));
            }

            return new SchedulerSettingsSnapshot(new SchedulerSettings(entries, capacity,
                root["enabled"].GetBoolean(), root["beep"].GetBoolean(),
                root["wakeUp"].GetBoolean(), version == 2 ? ReadOneShotStates(root["oneShotStates"]) : null),
                revision, version);
        }
        catch (Exception error) when (error is JsonException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException
            or KeyNotFoundException)
        {
            throw new InvalidDataException("予約設定の形式が不正です。", error);
        }
    }

    internal static byte[] Write(SchedulerSettings settings, Guid revision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (revision == Guid.Empty)
        {
            throw new ArgumentException("保存識別子は空にできません。", nameof(revision));
        }

        using var stream = new LimitedStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", 2);
        writer.WriteString("revision", revision);
        writer.WriteNumber("capacity", settings.Slots.Count);
        writer.WriteBoolean("enabled", settings.Enabled);
        writer.WriteBoolean("beep", settings.Beep);
        writer.WriteBoolean("wakeUp", settings.WakeUp);
        writer.WriteStartArray("slots");
        foreach (SchedulerEntry? entry in settings.Slots)
        {
            if (entry is null)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("id", entry.Id);
            writer.WriteBoolean("enabled", entry.Enabled);
            writer.WriteString("title", entry.Title);
            writer.WriteString("path", entry.Path);
            writer.WriteStartArray("arguments");
            foreach (string argument in entry.Arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteString("mode", entry.Mode == SchedulerExecutionMode.SameProcess
                ? "sameProcess" : "separateProcess");
            writer.WritePropertyName("condition");
            WriteCondition(writer, entry.Condition);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteOneShotStates(writer, settings.OneShotStates);
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static SchedulerEntry Entry(JsonElement value)
    {
        Dictionary<string, JsonElement> data = Object(value,
            "id", "enabled", "title", "path", "arguments", "mode", "condition");
        string path = Text(data["path"]);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("保存済みの予約パスは絶対パスが必要です。");
        }

        var arguments = new List<string>();
        foreach (JsonElement argument in data["arguments"].EnumerateArray())
        {
            arguments.Add(Text(argument));
        }

        SchedulerExecutionMode mode = Text(data["mode"]) switch
        {
            "sameProcess" => SchedulerExecutionMode.SameProcess,
            "separateProcess" => SchedulerExecutionMode.SeparateProcess,
            _ => throw new InvalidDataException("予約の実行方式が不正です。")
        };
        return new SchedulerEntry(Guid.Parse(Text(data["id"])), Text(data["title"]),
            path, arguments, mode, Condition(data["condition"]),
            System.IO.Path.GetPathRoot(path)!, data["enabled"].GetBoolean());
    }

    private static SchedulerCondition Condition(JsonElement value)
    {
        string kind = Text(value.GetProperty("kind"));
        string[] names = kind switch
        {
            "startup" => ["kind"],
            "interval" => ["kind", "unit", "value"],
            "time" => ["kind", "hour", "minute", "dayKind", "days"],
            "expression" => ["kind", "source"],
            "oneShot" => ["kind", "date", "hour", "minute"],
            "window" => ["kind", "title", "className"],
            _ => throw new InvalidDataException("予約条件の種類が不正です。")
        };
        Dictionary<string, JsonElement> data = Object(value, names);
        return kind switch
        {
            "startup" => new SchedulerStartupCondition(),
            "interval" => new SchedulerIntervalCondition(Text(data["unit"]) switch
            {
                "minute" => SchedulerIntervalUnit.Minute,
                "hour" => SchedulerIntervalUnit.Hour,
                _ => throw new InvalidDataException("予約の間隔単位が不正です。")
            }, data["value"].GetInt32()),
            "time" => new SchedulerTimeCondition(Time(data), Text(data["dayKind"]) switch
            {
                "everyDay" => SchedulerDayKind.EveryDay,
                "weekdays" => SchedulerDayKind.Weekdays,
                "monthDays" => SchedulerDayKind.MonthDays,
                _ => throw new InvalidDataException("予約の日付種別が不正です。")
            }, data["days"].EnumerateArray().Select(day => day.GetInt32())),
            "expression" => new SchedulerExpressionCondition(Text(data["source"])),
            "oneShot" => new SchedulerOneShotCondition(DateOnly.ParseExact(
                Text(data["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture), Time(data)),
            _ => new SchedulerWindowCondition(Text(data["title"]), Text(data["className"]))
        };
    }

    private static void WriteCondition(Utf8JsonWriter writer, SchedulerCondition condition)
    {
        writer.WriteStartObject();
        switch (condition)
        {
            case SchedulerStartupCondition:
                writer.WriteString("kind", "startup");
                break;
            case SchedulerIntervalCondition interval:
                writer.WriteString("kind", "interval");
                writer.WriteString("unit", interval.Unit == SchedulerIntervalUnit.Minute ? "minute" : "hour");
                writer.WriteNumber("value", interval.Value);
                break;
            case SchedulerTimeCondition time:
                writer.WriteString("kind", "time");
                WriteTime(writer, time.Time);
                writer.WriteString("dayKind", time.DayKind switch
                {
                    SchedulerDayKind.EveryDay => "everyDay",
                    SchedulerDayKind.Weekdays => "weekdays",
                    _ => "monthDays"
                });
                writer.WriteStartArray("days");
                foreach (int day in time.Days)
                {
                    writer.WriteNumberValue(day);
                }

                writer.WriteEndArray();
                break;
            case SchedulerExpressionCondition expression:
                writer.WriteString("kind", "expression");
                writer.WriteString("source", expression.Source);
                break;
            case SchedulerOneShotCondition oneShot:
                writer.WriteString("kind", "oneShot");
                writer.WriteString("date", oneShot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                WriteTime(writer, oneShot.Time);
                break;
            case SchedulerWindowCondition window:
                writer.WriteString("kind", "window");
                writer.WriteString("title", window.Title);
                writer.WriteString("className", window.ClassName);
                break;
            default:
                throw new InvalidDataException("未対応の予約条件です。");
        }

        writer.WriteEndObject();
    }

    private static TimeOnly Time(Dictionary<string, JsonElement> data) =>
        new(data["hour"].GetInt32(), data["minute"].GetInt32());

    private static void WriteTime(Utf8JsonWriter writer, TimeOnly time)
    {
        writer.WriteNumber("hour", time.Hour);
        writer.WriteNumber("minute", time.Minute);
    }

    private static string Text(JsonElement element)
    {
        string text = element.GetString() ?? throw new InvalidDataException("予約の文字列がnullです。");
        SchedulerValidation.Text(text);
        return text;
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, params string[] keys)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!keys.Contains(property.Name, StringComparer.Ordinal)
                || !result.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("予約設定に未知または重複の項目があります。");
            }
        }

        if (result.Count != keys.Length)
        {
            throw new InvalidDataException("予約設定の必須項目が不足しています。");
        }

        return result;
    }

    private sealed class LimitedStream : MemoryStream
    {
        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        private void Check(int count)
        {
            if (Length + count > SchedulerValidation.MaximumBytes)
            {
                throw new InvalidDataException("予約設定の保存上限は4MiBです。");
            }
        }
    }
}
