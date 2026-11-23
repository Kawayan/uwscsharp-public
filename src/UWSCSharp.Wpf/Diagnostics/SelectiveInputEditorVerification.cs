using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class SelectiveInputEditorVerification
{
    // 表示した同一編集画面で、実フックの終了回収と次の実行を検査します。
    internal static async Task<int> RunAsync(string directory)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("編集画面の入力検証はSandbox専用です。");
        }

        var checks = new List<object>();
        var errors = new List<Exception>();
        string? failure = null;
        var window = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add)
        {
            ShowActivated = false,
            Left = 700,
            Top = 20
        };
        var access = new MainWindow.PlaybackVerificationAccess(window);
        long handle = 0;
        try
        {
            using var request = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "request.json")));
            int mode = request.RootElement.GetProperty("mode").GetInt32();
            bool global = request.RootElement.GetProperty("global").GetBoolean();
            long receiver = request.RootElement.GetProperty("receiver").GetInt64();
            window.EditorPanel.IsExpanded = true;
            window.Show();
            StopWindow stop = access.EnsureStopWindow();
            stop.WindowStartupLocation = WindowStartupLocation.Manual;
            stop.Left = 700;
            stop.Top = 20;
            handle = new WindowInteropHelper(window).Handle.ToInt64();
            await MarkAsync("ready");

            string[] endings = ["release", "retry", "normal", "retry", "error", "retry", "cancel", "retry"];
            for (int run = 0; run < endings.Length; run++)
            {
                string ending = endings[run];
                string prefix = run + "-";
                await WaitAsync(() => File.Exists(Path.Combine(directory, prefix + "begin")));
                int errorCount = errors.Count;
                string target = global ? "0" : $"HNDTOID({receiver})";
                window.Editor.Text = $$"""
                    LOGPRINT(FALSE)
                    LOCKHARDEX({{target}},{{mode}})
                    Write("held")
                    WaitGate("finish")
                    {{(ending == "error" ? "DIM failure=1/0" : ending == "cancel" ? "SLEEP(60)" : ending == "release" ? "LOCKHARDEX()\nWrite(\"released\")\nWaitGate(\"exit\")" : "")}}
                    PROCEDURE Write(name)
                        DIM file=FOPEN("{{directory}}\{{prefix}}"+name,F_WRITE8)
                        FPUT(file,"ready",0)
                        FCLOSE(file)
                    FEND
                    PROCEDURE WaitGate(name)
                        WHILE !FOPEN("{{directory}}\{{prefix}}"+name,F_EXISTS)
                            SLEEP(0.01)
                        WEND
                    FEND
                    """;
                Require(window.PlayButton.IsEnabled && !window.Editor.IsReadOnly,
                    "編集と再生の開始条件");
                window.PlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() =>
                {
                    if (!access.IsPlaying)
                    {
                        throw new InvalidOperationException("登録通知前に実行が終了しました: "
                            + window.StatusLabel.Text + " / " + window.Output.Text);
                    }
                    return File.Exists(Path.Combine(directory, prefix + "held"));
                });
                Require(access.IsPlaying && window.StopButton.IsEnabled
                    && !window.PlayButton.IsEnabled && window.Editor.IsReadOnly,
                    "編集中の再生状態");
                if (ending == "cancel")
                {
                    await WaitAsync(() => File.Exists(Path.Combine(directory, prefix + "cancel")));
                    window.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }

                await WaitAsync(() => !access.IsPlaying);
                string status = ending == "cancel" ? "実行を停止しました"
                    : ending == "error" ? "エラー" : "実行が完了しました";
                Require(window.StatusLabel.Text == status, "終了状態: " + ending);
                Require(errors.Count == errorCount + (ending == "error" ? 1 : 0),
                    "例外通知の件数");
                if (ending == "error")
                {
                    Require(window.Output.Text.Contains("ゼロで除算できません。", StringComparison.Ordinal),
                        "ゼロ除算の診断を保持");
                }

                Require(access.PendingSelectiveInputCleanup == 0
                    && access.CanStartInput && window.PlayButton.IsEnabled
                    && window.RecordButton.IsEnabled && !window.StopButton.IsEnabled
                    && !window.InputCleanupButton.IsEnabled && !window.Editor.IsReadOnly,
                    "未回収0と編集・再生の復帰");
                Require(window.IsVisible && new WindowInteropHelper(window).Handle.ToInt64() == handle,
                    "同じ表示画面の維持");
                checks.Add(new
                {
                    run,
                    ending,
                    mode,
                    global,
                    handle,
                    status,
                    pending = access.PendingSelectiveInputCleanup,
                    errors = errors.Count - errorCount,
                    starts_restored = true
                });
                await MarkAsync(prefix + "ended");
                await WaitAsync(() => File.Exists(Path.Combine(directory, prefix + "observed")));
            }
        }
        catch (Exception error)
        {
            failure = error + "\n状態: " + window.StatusLabel.Text
                + "\n実行結果: " + window.Output.Text;
        }
        finally
        {
            access.RequestStop();
            try
            {
                await WaitAsync(() => !access.IsPlaying);
            }
            catch (Exception error)
            {
                failure = (failure ?? "") + "\n後始末: " + error;
            }
            window.Close();
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"),
            JsonSerializer.Serialize(new
            {
                result = failure is null ? "PASS" : "FAIL",
                checks,
                error = failure,
                process_id = Environment.ProcessId,
                handle,
                window_shown = true,
                native_hooks = true,
                button_events = true,
                physical_clicks = false
            }));
        return failure is null ? 0 : 1;

        Task MarkAsync(string name) => File.WriteAllTextAsync(Path.Combine(directory, name), "ready");

        static async Task WaitAsync(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(15))
                {
                    throw new TimeoutException("編集画面の入力検証期限");
                }
                await Task.Delay(10);
            }
        }
    }
}
