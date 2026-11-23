using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UWSCSharp.Wpf;

internal static partial class SchedulerSettingsJson
{
    private static IReadOnlyList<SchedulerOneShotState> ReadOneShotStates(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > SchedulerSettings.MaximumCapacity)
        {
            throw new InvalidDataException("一回予約の状態一覧が不正です。");
        }

        var states = new List<SchedulerOneShotState>();
        foreach (JsonElement value in element.EnumerateArray())
        {
            Dictionary<string, JsonElement> data = Object(value,
                "entryId", "date", "hour", "minute", "occurrenceId", "acceptedAtUtc", "status");
            var accepted = DateTimeOffset.ParseExact(Text(data["acceptedAtUtc"]), "O",
                CultureInfo.InvariantCulture);
            if (accepted.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("一回予約の受付時刻はUTCで保存してください。");
            }

            SchedulerOneShotStatus status = Text(data["status"]) switch
            {
                "accepted" => SchedulerOneShotStatus.Accepted,
                "started" => SchedulerOneShotStatus.Started,
                "succeeded" => SchedulerOneShotStatus.Succeeded,
                "failed" => SchedulerOneShotStatus.Failed,
                "cancelled" => SchedulerOneShotStatus.Cancelled,
                "unknown" => SchedulerOneShotStatus.Unknown,
                _ => throw new InvalidDataException("一回予約の結果が不正です。")
            };
            states.Add(new SchedulerOneShotState(Guid.Parse(Text(data["entryId"])),
                DateOnly.ParseExact(Text(data["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Time(data), Guid.Parse(Text(data["occurrenceId"])), accepted, status));
        }

        return states.AsReadOnly();
    }

    private static void WriteOneShotStates(Utf8JsonWriter writer,
        IEnumerable<SchedulerOneShotState> states)
    {
        writer.WriteStartArray("oneShotStates");
        foreach (SchedulerOneShotState state in states)
        {
            writer.WriteStartObject();
            writer.WriteString("entryId", state.EntryId);
            writer.WriteString("date", state.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            WriteTime(writer, state.Time);
            writer.WriteString("occurrenceId", state.OccurrenceId);
            writer.WriteString("acceptedAtUtc", state.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("status", state.Status switch
            {
                SchedulerOneShotStatus.Accepted => "accepted",
                SchedulerOneShotStatus.Started => "started",
                SchedulerOneShotStatus.Succeeded => "succeeded",
                SchedulerOneShotStatus.Failed => "failed",
                SchedulerOneShotStatus.Cancelled => "cancelled",
                SchedulerOneShotStatus.Unknown => "unknown",
                _ => throw new InvalidDataException("一回予約の結果が不正です。")
            });
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
