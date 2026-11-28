using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;
using static UWSCSharp.Wpf.SettingsVerification;

namespace UWSCSharp.Wpf;

internal static class RecordingClipboardVerification
{
    private static async Task<string[]> VerifyRecordingClipboardAsync(string directory)
    {
        var playback = new ButtonPlayback();
        var clipboard = new ClipboardBackend();
        var checks = new List<string>();
        var errors = new List<Exception>();
        var device = new ClipboardRecordingDevice();
        var controller = new RecordingController(() => device, TimeSpan.FromMilliseconds(300));
        int writes = 0;
        bool failAfterEmpty = false;
        bool allow = true;
        bool closed = false;
        MainWindow.RecordingVerificationAccess access = null!;
        var window = new MainWindow(false, recordingController: controller, confirmDiscard: () => allow,
            reportError: errors.Add, windowsAutomationFactory: _ => playback,
            copyRecordingText: text =>
            {
                Require(access.Source == text && access.IsModified && access.ScriptPath is null,
                    "コピー前に新しい未保存の記録を反映");
                writes++;
                (failAfterEmpty ? new ClipboardBackend(new FailedSetApi()) : clipboard).WriteText(text, default);
            });
        access = new MainWindow.RecordingVerificationAccess(window);
        window.Closed += (_, _) => closed = true;
        string settingsPath = MainWindow.RecordingVerificationAccess.SettingsPath;
        byte[]? settings = File.Exists(settingsPath)
            ? await File.ReadAllBytesAsync(settingsPath) : null;
        try
        {
            window.Show();
            clipboard.WriteText("元の内容", default);
            access.SetCopyOption(false);
            await StartAsync();
            await FinishAsync(false);
            Require(clipboard.ReadText(default) == "元の内容", "既定無効時は旧内容保持");
            checks.Add("copy-default-off-preserves-clipboard");

            await SaveOptionAsync("CopyRecordingCheckBox", true);
            Require(RecordingSettings.Load(settingsPath).CopyAfterRecording, "設定保存");
            await access.EditRecordingSettingsAsync(dialog =>
            {
                var option = (CheckBox)dialog.FindName("CopyRecordingCheckBox");
                Require(option.IsChecked == true, "保存値を設定画面に表示");
                option.IsChecked = false;
                dialog.Close();
                return Task.CompletedTask;
            });
            Require(access.Settings.CopyAfterRecording
                && RecordingSettings.Load(settingsPath).CopyAfterRecording, "取消で保存値を保持");
            using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await access.EditRecordingSettingsAsync(dialog =>
                {
                    var option = (CheckBox)dialog.FindName("CopyRecordingCheckBox");
                    option.IsChecked = false;
                    ClickSave(dialog);
                    Require(dialog.IsVisible && Error(dialog).Text.Contains("記録設定を保存できませんでした")
                        && option.IsChecked == false, "保存失敗時に設定画面と編集内容を保持");
                    dialog.Close();
                    return Task.CompletedTask;
                });
                Require(access.Settings.CopyAfterRecording
                    && RecordingSettings.Load(settingsPath).CopyAfterRecording, "保存失敗時は適用済みの設定を保持");
            }
            checks.Add("copy-setting-save-and-failure-rollback");

            await SaveOptionAsync("BackgroundRecordingCheckBox", true);
            Require(access.Settings.BackgroundPlayback
                && RecordingSettings.Load(settingsPath).BackgroundPlayback,
                "背景再生用記録の設定を保存");
            var reopened = new MainWindow(false);
            try
            {
                reopened.Show();
                var reopenedAccess = new MainWindow.RecordingVerificationAccess(reopened);
                Require(reopenedAccess.Settings.BackgroundPlayback,
                    "背景再生用記録の保存値を読み込む");
            }
            finally
            {
                reopened.Close();
            }

            await StartAsync();
            Require(!access.CanEditRecordingSettings, "記録中は背景再生用設定を変更不可");
            await FinishAsync(true);
            Require(access.CanEditRecordingSettings, "停止後に背景再生用設定を変更可能");
            await SaveOptionAsync("BackgroundRecordingCheckBox", false);
            Require(!RecordingSettings.Load(settingsPath).BackgroundPlayback,
                "背景再生用記録の無効化を保存");
            checks.Add("background-recording-setting-save-and-reload");

            device = new()
            {
                Source = "PRINT \"日本語😀\"\r\n"
            };
            await StartAsync();
            Require(!access.CanEditRecordingSettings, "記録中は設定変更不可");
            await FinishAsync(true);
            Require(clipboard.ReadText(default) == device.Source, "Unicodeと末尾の保持");
            checks.Add("copy-manual-stop-exact-unicode-source");

            device = new()
            {
                Source = "PRINT 2\r\n"
            };
            await StartAsync();
            int before = writes;
            access.ToggleRecording();
            await FinishedAsync();
            Require(writes == before + 1 && clipboard.ReadText(default) == device.Source, "記録ボタンで終了");
            checks.Add("copy-record-toggle-next-session");

            foreach (ClipboardRecordingDevice excluded in new ClipboardRecordingDevice[]
            {
                new() { Source = "" }, new() { Source = " \t\r\n" },
                new() { Overflowed = true }, new() { Warning = "検証用の欠落警告" }
            })
            {
                device = excluded;
                clipboard.WriteText("除外時の旧内容", default);
                await StartAsync();
                await FinishAsync(false);
                Require(clipboard.ReadText(default) == "除外時の旧内容", "不完全な記録はコピーしない");
            }
            checks.Add("copy-empty-overflow-warning-excluded");

            const string omitted = "背景再生に変換できない操作を省略しました。";
            device = new()
            {
                Source = "// 警告: " + omitted + Environment.NewLine,
                Warning = omitted
            };
            await StartAsync();
            await FinishAsync(false);
            Require(access.Status.Contains(omitted, StringComparison.Ordinal),
                "背景再生用の記録で省略した操作を状態欄に表示");
            Require(clipboard.ReadText(default) == "除外時の旧内容",
                "警告付きの記録ではクリップボードを保持");
            checks.Add("background-recording-warning-shown");

            access.SetCopyOption(false);
            device = new();
            await StartAsync();
            await FinishAsync(false);
            Require(clipboard.ReadText(default) == "除外時の旧内容", "次の無効設定を尊重");
            checks.Add("copy-disabled-new-session-does-not-replay");
            access.SetCopyOption(true);

            allow = false;
            access.SetSource("開始を取り消す原稿");
            before = writes;
            access.ToggleRecording();
            Require(!controller.HasRecorder && writes == before && access.Source == "開始を取り消す原稿",
                "開始確認の取消");
            allow = true;
            checks.Add("copy-start-confirmation-cancelled");

            device = new()
            {
                StartError = new IOException("開始失敗"),
                DisposeError = new IOException("回収失敗")
            };
            int previousErrors = errors.Count;
            access.ToggleRecording();
            await RecordingClipboardVerification.WaitAsync(() => errors.Count == previousErrors + 1 && !controller.IsStarting);
            Require(controller.RecoveryPending, "失敗した開始の回収待ち");
            device.StartError = null;
            device.DisposeError = null;
            await FinishAsync(false);
            checks.Add("copy-start-failure-recovery-excluded");

            device = new()
            {
                StopError = new IOException("停止失敗")
            };
            await StartAsync();
            string original = access.Source;
            previousErrors = errors.Count;
            access.ClickStop();
            await RecordingClipboardVerification.WaitAsync(() => errors.Count == previousErrors + 1 && !controller.IsStopping);
            Require(access.Source == original && controller.HasRecorder, "停止失敗時は元の原稿保持");
            device.StopError = null;
            await FinishAsync(false);
            checks.Add("copy-stop-failure-retry-excluded");

            device = new();
            await StartAsync();
            before = writes;
            device.Running = false;
            await FinishedAsync();
            Require(writes == before, "監視での停止検出はコピーしない");
            checks.Add("copy-worker-monitor-excluded");

            clipboard.WriteText("競合前の内容", default);
            string ready = Path.Combine(directory, "clipboard-lock-ready.json");
            string release = Path.Combine(directory, "clipboard-lock-release.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--verify-recording-clipboard-lock");
            start.ArgumentList.Add(ready);
            start.ArgumentList.Add(release);
            using (Process child = Process.Start(start)!)
            {
                try
                {
                    await RecordingClipboardVerification.WaitAsync(() => File.Exists(ready) || child.HasExited);
                    Require(File.Exists(ready) && !child.HasExited, "所有するロック子プロセスの準備");
                    using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
                    nint handle = (nint)receipt.RootElement.GetProperty("handle").GetInt64();
                    Require(child.Id != Environment.ProcessId
                        && RecordingClipboardVerification.IsLockedBy(handle, child.Id), "別PIDが実クリップボードを保持");
                    device = new();
                    await StartAsync();
                    previousErrors = errors.Count;
                    await FinishAsync(true);
                    Require(errors.Count == previousErrors + 1 && access.Status.Contains("残っています", StringComparison.Ordinal),
                        "競合失敗は記録結果と分離して通知");
                }
                finally
                {
                    await File.WriteAllTextAsync(release, "release");
                    try
                    {
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    finally
                    {
                        if (!child.HasExited)
                        {
                            child.Kill(entireProcessTree: true);
                            await child.WaitForExitAsync();
                        }
                    }
                }
                Require(child.ExitCode == 0, "ロック所有者の解放");
            }
            before = writes;
            await Task.Delay(100);
            Require(clipboard.ReadText(default) == "競合前の内容" && writes == before, "開けない失敗で旧内容保持・自動再試行なし");
            checks.Add("copy-native-other-process-lock-preserves-new-draft");

            failAfterEmpty = true;
            previousErrors = errors.Count;
            device = new();
            await StartAsync();
            await FinishAsync(true);
            Require(errors.Count == previousErrors + 1 && clipboard.ReadText(default) == "", "Empty後の失敗で旧内容を復元しない");
            failAfterEmpty = false;
            checks.Add("copy-injected-set-failure-after-native-empty");

            device = new()
            {
                Source = "PRINT 99\r\n"
            };
            await StartAsync();
            await FinishAsync(true);
            Require(clipboard.ReadText(default) == device.Source, "コピー失敗後も新しい記録をコピー");
            checks.Add("copy-fresh-session-after-clipboard-failure");

            var button = new RecordedButtonClick(
                new RecordedWindow(1, "検証", "Static", 0, 0, 100, 100), "実行", 1);
            device = new()
            {
                Source = button.Command() + Environment.NewLine
            };
            await StartAsync();
            await FinishAsync(true);
            string script = Path.Combine(directory, "recorded-button.uws");
            access.SaveScript(script);
            Require(await File.ReadAllTextAsync(script) == device.Source
                && !access.IsModified && access.ScriptPath == script, "記録の保存");
            Require(await access.PlayAsync() == 0 && playback.Clicks == 1,
                "保存した記録の再生");
            checks.Add("recorded-button-save-and-playback");

            access.SetSource("SLEEP(100)");
            Task<int> playing = access.PlayAsync();
            try
            {
                Require(!access.CanEditRecordingSettings, "再生中も設定変更不可");
            }
            finally
            {
                access.CancelPlayback();
                await playing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            checks.Add("copy-option-disabled-during-playback");

            device = new();
            await StartAsync();
            before = writes;
            string priorClipboard = clipboard.ReadText(default);
            window.Close();
            await RecordingClipboardVerification.WaitAsync(() => closed);
            Require(writes == before && clipboard.ReadText(default) == priorClipboard && access.Source == device.Source,
                "終了時の回収は新原稿だけ残しコピーしない");
            checks.Add("copy-owner-closing-excluded");
            return [.. checks];
        }
        finally
        {
            device.StopError = null;
            device.DisposeError = null;
            if (controller.HasRecorder && !controller.IsStarting && !controller.IsStopping)
            {
                await controller.StopAsync(RecordingStopReason.OwnerClosing);
            }
            if (!closed)
            {
                access.DiscardDraft();
                window.Close();
            }
            if (settings is null)
            {
                File.Delete(settingsPath);
            }
            else
            {
                await File.WriteAllBytesAsync(settingsPath, settings);
            }
        }

        Task SaveOptionAsync(string name, bool enabled) =>
            access.EditRecordingSettingsAsync(async dialog =>
            {
                ((CheckBox)dialog.FindName(name)).IsChecked = enabled;
                ClickSave(dialog);
                await WaitAsync(() => !dialog.IsVisible);
            });

        async Task StartAsync()
        {
            access.ToggleRecording();
            await RecordingClipboardVerification.WaitAsync(() => controller.IsRunning && !controller.IsStarting);
        }

        async Task FinishedAsync()
        {
            await RecordingClipboardVerification.WaitAsync(() => !controller.HasRecorder && !controller.IsStopping
                && access.Source == device.Source && access.Status.StartsWith("記録終了", StringComparison.Ordinal));
            await Task.Yield();
        }

        async Task FinishAsync(bool copyAttempted)
        {
            int before = writes;
            access.ClickStop();
            await FinishedAsync();
            Require(writes == before + (copyAttempted ? 1 : 0), "コピー試行の件数");
            Require(access.CanEditRecordingSettings, "停止後に設定編集へ戻る");
        }
    }

    private sealed class FailedSetApi : ClipboardApi
    {
        internal override nint Set(uint format, nint memory)
        {
            Marshal.SetLastPInvokeError(5);
            return 0;
        }
    }

    private sealed class ClipboardRecordingDevice : IRecordingDevice
    {
        internal string Source { get; init; } = "PRINT 1";
        internal bool Running
        {
            get; set;
        }
        internal Exception? StartError
        {
            get; set;
        }
        internal Exception? StopError
        {
            get; set;
        }
        internal Exception? DisposeError
        {
            get; set;
        }
        public bool IsRunning => Running;
        public bool WaitingForInitialRelease => false;
        public bool Overflowed
        {
            get; init;
        }
        public string Warning { get; init; } = "";
        public Task StartAsync()
        {
            Running = true;
            return StartError is null ? Task.CompletedTask : Task.FromException(StartError);
        }
        public string Stop()
        {
            if (StopError is not null)
            {
                throw StopError;
            }
            Running = false;
            return Source;
        }
        public void Dispose()
        {
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
            Running = false;
        }
    }

    private sealed class ButtonPlayback : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Clicks
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (name == BuiltinFunctionNames.GetId)
            {
                return ValueTask.FromResult(Value.Numeric(1));
            }

            Require(name == BuiltinFunctionNames.ClkItem && arguments.Count == 5
                && arguments[0].Number == 1 && arguments[1].Text == "実行",
                "記録したボタンの再生");
            Clicks++;
            return ValueTask.FromResult(Value.Logical(true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static async Task<int> RunAsync(string receipt)
    {
        RequireSandbox();
        try
        {
            string[] checks = await VerifyRecordingClipboardAsync(Path.GetDirectoryName(receipt)!);
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

    internal static async Task<int> RunLockAsync(string ready, string release)
    {
        RequireSandbox();
        var api = new ClipboardApi();
        nint handle = api.CreateOwner();
        bool opened = false;
        try
        {
            if (handle == 0 || !(opened = api.Open(handle)))
            {
                throw new InvalidOperationException("検証用のクリップボードロックを取得できません。");
            }
            string pending = ready + ".tmp";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
            {
                handle = handle.ToInt64(),
                pid = Environment.ProcessId
            }));
            File.Move(pending, ready);
            await WaitAsync(() => File.Exists(release), TimeSpan.FromSeconds(20));
            return 0;
        }
        finally
        {
            if (opened)
            {
                api.Close();
            }
            if (handle != 0)
            {
                api.DestroyOwner(handle);
            }
        }
    }

    internal static async Task WaitAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException("記録後コピーの検証状態を確認できません。");
            }
            await Task.Delay(10);
        }
    }

    internal static bool IsLockedBy(nint handle, int pid)
    {
        Native.GetWindowThreadProcessId(handle, out uint actual);
        return GetOpenClipboardWindow() == handle && actual == (uint)pid;
    }

    private static void RequireSandbox()
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("記録後コピーのGUI検証はWindows Sandbox内で実行してください。");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetOpenClipboardWindow();
}
