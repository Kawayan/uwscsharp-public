using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ScriptDropVerification
{
    private static async Task<string[]> VerifyScriptDropAsync(string directory)
    {
        string file = Path.Combine(directory, "日本語 script.uws");
        string slow = Path.Combine(directory, "待機.uws");
        await File.WriteAllTextAsync(file, "PRINT GET_UWSC_NAME\nPRINT LENGTH(PARAM_STR)");
        await File.WriteAllTextAsync(slow, "SLEEP(30)");
        List<string> checks = [];
        List<Exception> errors = [];
        bool allow = false;
        bool reenter = false;
        Task<int?>? nested = null;
        Task<int?>? active = null;
        var recordingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var recordingStop = new ManualResetEventSlim();
        var recorder = new ScriptDropRecordingDevice(recordingStart.Task, recordingStop);
        var controller = new RecordingController(() => recorder, TimeSpan.FromSeconds(30));
        Task? starting = null;
        Task<RecordingResult?>? stopping = null;
        MainWindow? window = null;
        window = new MainWindow(commandLine: false,
            recordingController: controller,
            confirmDiscard: () =>
            {
                if (reenter)
                {
                    nested = window!.RunDroppedScriptAsync(slow);
                }

                return allow;
            }, reportError: errors.Add)
        {
            ShowActivated = false
        };
        var access = new MainWindow.ScriptDropVerificationAccess(window);
        try
        {
            window.Show();
            access.SetDraft("未保存の原稿", "元のパス");
            Require(await window.RunDroppedScriptAsync(file) is null, "確認取消の結果");
            RequireOriginal();
            checks.Add("discard-cancellation-preserves-editor");

            allow = true;
            Require(await window.RunDroppedScriptAsync(file + ".missing") == 1, "消失ファイルの失敗");
            RequireOriginal();
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(await window.RunDroppedScriptAsync(file) == 1, "読取ロックの失敗");
            }

            RequireOriginal();
            Require(errors.Count == 2 && errors.All(error => error is IOException), "読取失敗の通知");
            checks.Add("read-failures-preserve-editor-and-report-error");

            reenter = true;
            Require(await window.RunDroppedScriptAsync(file) == 0, "単一ファイルの再生");
            Require(nested is not null && await nested is null, "確認中の再入拒否");
            checks.Add("confirmation-reentry-rejected");
            reenter = false;
            Require(access.ScriptPath == file && !access.IsModified, "読込済みパスと編集状態");
            Require(access.Output.Contains("日本語 script.uws", StringComparison.Ordinal)
                && access.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("0"),
                "スクリプト名と空の引数");
            checks.Add("single-script-path-and-empty-arguments");

            active = window.RunDroppedScriptAsync(slow);
            Require(access.IsPlaying, "実行中の状態");
            Require(await window.RunDroppedScriptAsync(file) is null, "実行中の受入拒否");
            Require(access.ScriptPath == slow, "実行中の原稿保持");
            access.CancelActivePlayback();
            Require(await active.WaitAsync(TimeSpan.FromSeconds(10)) == 130, "再生取消");
            Require(await window.RunDroppedScriptAsync(file) == 0, "取消後の再実行");
            checks.Add("busy-rejection-cancellation-and-rerun");

            access.SetDraft("未保存の原稿", "元のパス");
            starting = controller.StartAsync();
            Require(controller.IsStarting, "記録開始待ち");
            Require(await window.RunDroppedScriptAsync(file) is null, "記録開始待ちの拒否");
            RequireOriginal();
            checks.Add("recording-start-pending-preserves-editor");
            recordingStart.SetResult();
            await starting;
            Require(controller.IsRunning, "記録中の状態");
            Require(await window.RunDroppedScriptAsync(file) is null, "記録中の拒否");
            RequireOriginal();
            checks.Add("recording-active-preserves-editor");
            stopping = controller.StopAsync();
            Require(controller.IsStopping, "記録終了待ち");
            Require(await window.RunDroppedScriptAsync(file) is null, "記録終了待ちの拒否");
            RequireOriginal();
            recordingStop.Set();
            await stopping;
            checks.Add("recording-stop-pending-preserves-editor");

            access.ExpandEditor();
            window.UpdateLayout();
            Require(window.AllowDrop && access.DropTarget.AllowDrop, "ドロップ許可の継承");
            var files = new DataObject(DataFormats.FileDrop, new[] { file, slow });
            DragEventArgs preview = RaiseDropEvent(files, DragDrop.PreviewDragOverEvent);
            Require(preview.Handled && preview.Effects == DragDropEffects.Copy, "編集欄の受入表示");
            allow = false;
            RaiseDropEvent(files, DragDrop.PreviewDropEvent);
            await WaitForDropAsync();
            RequireOriginal();
            checks.Add("routed-drop-discard-cancellation-preserves-editor");

            allow = true;
            RaiseDropEvent(files, DragDrop.PreviewDropEvent);
            await WaitForDropAsync();
            Require(access.ScriptPath == file && !access.IsModified
                && access.Source == await File.ReadAllTextAsync(file), "先頭ファイルだけを読込み");
            Require(access.Output.Contains("日本語 script.uws", StringComparison.Ordinal), "経路経由の再生");
            checks.Add("routed-editor-drop-plays-first-path");

            string other = Path.Combine(directory, "別の script.uws");
            await File.WriteAllTextAsync(other, "PRINT GET_UWSC_NAME\nPRINT LENGTH(PARAM_STR)");
            RaiseDropEvent(new DataObject(DataFormats.FileDrop, new[] { other, file }),
                DragDrop.PreviewDropEvent);
            await WaitForDropAsync();
            Require(access.ScriptPath == other && access.Output.Contains("別の script.uws", StringComparison.Ordinal),
                "受信順の先頭へ切替え");
            Require(access.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("0"),
                "後続ファイルを引数へ渡さない");
            checks.Add("routed-multiple-drop-keeps-order-and-empty-arguments");

            access.SetDraft("未保存の原稿", "元のパス");
            foreach (DataObject data in new[]
            {
                new DataObject(DataFormats.UnicodeText, "挿入しない文字列"),
                new DataObject(DataFormats.FileDrop, Array.Empty<string>())
            })
            {
                DragEventArgs rejected = RaiseDropEvent(data, DragDrop.PreviewDragOverEvent);
                Require(rejected.Handled && rejected.Effects == DragDropEffects.None, "不適合形式の受入拒否");
                rejected = RaiseDropEvent(data, DragDrop.PreviewDropEvent);
                Require(rejected.Handled && rejected.Effects == DragDropEffects.None, "不適合形式のドロップ拒否");
                RequireOriginal();
            }

            DragEventArgs moveOnly = RaiseDropEvent(files, DragDrop.PreviewDropEvent, DragDropEffects.Move);
            Require(moveOnly.Handled && moveOnly.Effects == DragDropEffects.None, "移動のみのデータを拒否");
            RequireOriginal();
            checks.Add("routed-unsupported-data-and-move-preserve-editor");

            int previousErrors = errors.Count;
            foreach (string invalid in new[] { file + ".missing", directory })
            {
                RaiseDropEvent(new DataObject(DataFormats.FileDrop, new[] { invalid, file }),
                    DragDrop.PreviewDropEvent);
                await WaitForDropAsync();
                RequireOriginal();
            }

            Require(errors.Count == previousErrors + 2, "消失・フォルダーの失敗通知");
            checks.Add("routed-invalid-first-path-does-not-fall-through");

            RaiseDropEvent(new DataObject(DataFormats.FileDrop, new[] { slow }), DragDrop.PreviewDropEvent);
            Require(access.IsPlaying, "経路経由の待機再生");
            DragEventArgs busy = RaiseDropEvent(files, DragDrop.PreviewDragOverEvent);
            Require(busy.Handled && busy.Effects == DragDropEffects.None, "実行中の受入表示");
            busy = RaiseDropEvent(files, DragDrop.PreviewDropEvent);
            Require(busy.Handled && busy.Effects == DragDropEffects.None && access.ScriptPath == slow,
                "実行中の経路による拒否");
            access.CancelActivePlayback();
            await WaitForDropAsync();
            checks.Add("routed-busy-drop-rejected-and-cancelled");

            string targetDirectory = Path.Combine(directory, "リンクの対象 folder");
            Directory.CreateDirectory(targetDirectory);
            string target = Path.Combine(targetDirectory, "対象 script.uws");
            string link = Path.Combine(directory, "起動 link.lnk");
            await File.WriteAllTextAsync(target, ScriptDropShortcutVerification.ShortcutProbeSource);
            ScriptDropShortcutVerification.CreateDropShortcut(link, target, "\"日本語 alpha\" beta", directory);
            string processDirectory = Environment.CurrentDirectory;
            access.SetDraft("未保存の原稿", "元のパス");
            allow = false;
            Require(await window.RunDroppedScriptAsync(link) is null, "リンクの未保存確認取消");
            RequireOriginal();
            checks.Add("shortcut-discard-cancellation-preserves-editor");

            allow = true;
            RaiseDropEvent(new DataObject(DataFormats.FileDrop, new[] { link, file }), DragDrop.PreviewDropEvent);
            await WaitForDropAsync();
            Require(access.ScriptPath == target && access.Output.Contains("COUNT=2", StringComparison.Ordinal)
                && access.Output.Contains("ARG[0]=[日本語 alpha]", StringComparison.Ordinal)
                && access.Output.Contains("ARG[1]=[beta]", StringComparison.Ordinal)
                && access.Output.Contains("DIR=" + targetDirectory, StringComparison.Ordinal),
                "リンクの先頭選択・対象・引数・基準フォルダー");
            Require(Environment.CurrentDirectory == processDirectory, "プロセスのカレントディレクトリ保持");
            checks.Add("shortcut-routed-target-arguments-and-scoped-directory");

            access.SetDraft("未保存の原稿", "元のパス");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(await window.RunDroppedScriptAsync(link) == 1, "リンク先の読取り失敗");
                RequireOriginal();
            }
            checks.Add("shortcut-target-read-failure-preserves-editor");

            string malformed = Path.Combine(directory, "壊れた link.lnk");
            await File.WriteAllTextAsync(malformed, "not a shortcut");
            string invalidTarget = Path.Combine(directory, "非スクリプト.lnk");
            ScriptDropShortcutVerification.CreateDropShortcut(invalidTarget, Environment.ProcessPath!, "", directory);
            string missingTarget = Path.Combine(directory, "対象消失.lnk");
            ScriptDropShortcutVerification.CreateDropShortcut(missingTarget, target + ".missing.uws", "", directory);
            string folderTarget = Path.Combine(directory, "フォルダー.lnk");
            string folderAsScript = Path.Combine(directory, "folder.uws");
            Directory.CreateDirectory(folderAsScript);
            ScriptDropShortcutVerification.CreateDropShortcut(folderTarget, folderAsScript, "", directory);
            // 実リンクの連鎖は、この調査コマンドの確認範囲に含めません。
            previousErrors = errors.Count;
            foreach (string invalid in new[] { link + ".missing.lnk", malformed, invalidTarget, missingTarget, folderTarget })
            {
                RaiseDropEvent(new DataObject(DataFormats.FileDrop, new[] { invalid, link }), DragDrop.PreviewDropEvent);
                await WaitForDropAsync();
                Require(access.Source == "未保存の原稿"
                    && access.ScriptPath == "元のパス" && access.IsModified,
                    "無効リンクで元の編集内容を保持: " + Path.GetFileName(invalid)
                    + ", 現在のパス=" + access.ScriptPath);
            }
            Require(errors.Count == previousErrors + 5, "無効リンク5条件の失敗通知");
            checks.Add("shortcut-invalid-first-path-never-falls-through");

            string emptyArgument = Path.Combine(directory, "空引数.lnk");
            ScriptDropShortcutVerification.CreateDropShortcut(emptyArgument, target, "\"\"", directory);
            Require(await window.RunDroppedScriptAsync(emptyArgument) == 0
                && access.Output.Contains("COUNT=1", StringComparison.Ordinal)
                && access.Output.Contains("ARG[0]=[]", StringComparison.Ordinal), "空引数の保持");
            checks.Add("shortcut-empty-argument-preserved");

            active = window.RunDroppedScriptAsync(slow);
            using (var locked = new FileStream(link, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(await window.RunDroppedScriptAsync(link) is null, "実行中はリンク読取り前に拒否");
            }
            access.CancelActivePlayback();
            Require(await active.WaitAsync(TimeSpan.FromSeconds(10)) == 130, "リンク拒否後の停止");
            checks.Add("shortcut-busy-rejected-before-metadata-read");
            return [.. checks];
        }
        finally
        {
            access.CancelPlayback();
            recordingStart.TrySetResult();
            recordingStop.Set();
            if (starting is not null)
            {
                await starting;
            }

            if (stopping is not null)
            {
                await stopping;
            }

            if (controller.HasRecorder)
            {
                await controller.StopAsync();
            }
            if (active is not null)
            {
                await active.WaitAsync(TimeSpan.FromSeconds(10));
            }

            access.DiscardDraft();
            window.Close();
        }

        void RequireOriginal() => Require(access.Source == "未保存の原稿"
            && access.ScriptPath == "元のパス" && access.IsModified, "元の編集内容の保持");

        DragEventArgs RaiseDropEvent(DataObject data, RoutedEvent routedEvent,
            DragDropEffects allowed = DragDropEffects.Copy)
        {
            // WPFの内部コンストラクターで検証用イベントを作り、実際の経路へ送ります。
            // OLEやExplorerによる実ドラッグの確認とは区別します。
            ConstructorInfo constructor = typeof(DragEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects),
                    typeof(DependencyObject), typeof(Point)], null)!;
            var args = (DragEventArgs)constructor.Invoke(
                [data, DragDropKeyStates.None, allowed, access.DropTarget, new Point(5, 5)]);
            args.RoutedEvent = routedEvent;
            access.DropTarget.RaiseEvent(args);
            return args;
        }

        async Task WaitForDropAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (access.IsReceiving)
            {
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    private sealed class ScriptDropRecordingDevice(Task start, ManualResetEventSlim stop) : IRecordingDevice
    {
        public bool IsRunning
        {
            get; private set;
        }

        public bool WaitingForInitialRelease => false;

        public bool Overflowed => false;

        public Task StartAsync()
        {
            IsRunning = true;
            return start;
        }

        public string Stop()
        {
            if (!stop.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("検証用記録器の停止待機が終了しません。");
            }

            IsRunning = false;
            return "";
        }

        public void Dispose() => IsRunning = false;
    }

    internal static async Task<int> RunAsync(string receipt)
    {
        if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ドロップ再生の検証はWindows Sandbox内で実行してください。");
        }

        try
        {
            string[] checks = await VerifyScriptDropAsync(Path.GetDirectoryName(receipt)!);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks,
                window_shown = true,
                input_injection = false
            }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                error = error.ToString()
            }));
            return 1;
        }
    }
}
