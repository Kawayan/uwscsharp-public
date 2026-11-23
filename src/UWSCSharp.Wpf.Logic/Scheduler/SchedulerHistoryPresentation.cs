using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace UWSCSharp.Wpf;

/// <summary>
/// 保存済みの観測だけを表示し、現在の実行状態や定義を推測しません。
/// </summary>
internal static class SchedulerHistoryPresentation
{
    private static readonly JsonSerializerOptions QuotedText = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static string Outcome(SchedulerExecutionOutcome? value) => value switch
    {
        SchedulerExecutionOutcome.Succeeded => "成功",
        SchedulerExecutionOutcome.Failed => "失敗",
        SchedulerExecutionOutcome.Cancelled => "取消完了",
        SchedulerExecutionOutcome.Unknown => "結果不明",
        _ => "結果未記録"
    };

    internal static string Summary(SchedulerHistorySummary value) =>
        Time(value.AcceptedAt) + " / " + value.Title + " / " + Outcome(value.Outcome)
        + (value.ResourcesRecovered ? " / 回収済み" : " / 回収未確認");

    internal static string Details(SchedulerHistoryRecord value)
    {
        var text = new StringBuilder();
        text.AppendLine("題名: " + value.Title);
        text.AppendLine("対象: " + value.Path);
        text.AppendLine("方式: " + (value.Mode == SchedulerExecutionMode.SameProcess ? "同一プロセス" : "別プロセス"));
        text.AppendLine("受付: " + Time(value.AcceptedAt));
        text.AppendLine("開始: " + Time(value.StartedAt));
        text.AppendLine("開始情報: " + (value.Started ? "あり" : "未確認"));
        text.AppendLine("結果受取: " + Time(value.ResultObservedAt));
        text.AppendLine("終了確認: " + Time(value.EndedAt));
        text.AppendLine("結果: " + Outcome(value.Outcome));
        text.AppendLine("終了コード: " + (value.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "未確認"));
        text.AppendLine("資源回収: " + (value.ResourcesRecovered ? "確認済み" : "未確認"));
        text.AppendLine("強制停止: " + (value.Forced ? "あり" : "なし"));
        text.AppendLine("発生識別子: " + value.OccurrenceId.ToString("N"));
        text.AppendLine("受付時の行識別子: " + value.EntryId.ToString("N"));
        text.AppendLine("監視世代: " + value.Generation.ToString("N"));
        if (value.Window is { } window)
        {
            text.AppendLine("捕捉先（保存時の観測。現在の有効性は未確認）:");
            text.AppendLine("HWND値: " + window.Handle.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("PID: " + window.ProcessId.ToString(CultureInfo.InvariantCulture)
                + " / TID: " + window.ThreadId.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("窓の観測: " + Time(window.ObservedAt));
        }
        else
        {
            text.AppendLine("捕捉先: 未記録");
        }
        text.AppendLine("引数（引用符付き・改行などはエスケープ表示）: " + value.Arguments.Count.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < value.Arguments.Count; index++)
        {
            text.AppendLine($"  {index + 1}: " + JsonSerializer.Serialize(value.Arguments[index], QuotedText));
        }
        text.AppendLine("診断:");
        text.Append(value.Diagnostic ?? "（保存された診断はありません）");
        return text.ToString();
    }

    private static string Time(DateTimeOffset? value) => value?.ToUniversalTime()
        .ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture) ?? "未確認";
}
