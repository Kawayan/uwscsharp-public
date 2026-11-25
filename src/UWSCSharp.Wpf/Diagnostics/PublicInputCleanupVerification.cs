using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

/// <summary>
/// 公開スクリプトの終了失敗から実WPF部品での再回収までを検査します。
/// </summary>
internal static class PublicInputCleanupVerification
{
    internal static Task<int> RunAsync(string receipt) =>
        RunAsync(receipt, verifyExitDialog: false);

    internal static Task<int> RunExitDialogAsync(string receipt) =>
        RunAsync(receipt, verifyExitDialog: true);

    private static async Task<int> RunAsync(string receipt, bool verifyExitDialog)
    {
        var checks = new List<object>();
        string? failure = null;
        try
        {
            Require(SandboxEnvironment.IsCurrent, "Sandbox内の検証");
            foreach (int mode in new[] { 0, 1, 2 })
            {
                await VerifyModeAsync(mode, receipt + ".uws", checks,
                    verifyExitDialog && mode == 0);
            }
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = failure is null ? "PASS" : "FAIL",
            checks,
            error = failure,
            window_shown = true,
            input_injection = false,
            limits = verifyExitDialog
                ? "登録・解除は偽フック。再回収はClickイベント、終了確認はWin32メッセージで操作した。"
                : "登録・解除は偽フック。再回収はClickイベントで操作した。"
        }));
        return failure is null ? 0 : 1;
    }

    private static async Task VerifyModeAsync(int mode, string transferScript,
        ICollection<object> checks, bool verifyExitDialog)
    {
        var hooks = new List<Hook>();
        var errors = new List<Exception>();
        using var finishRecovery = new ManualResetEventSlim();
        int discards = 0;
        WindowsSelectiveInputCleanup? cleanup = null;
        var window = new MainWindow(false, confirmDiscard: () =>
        {
            discards++;
            return true;
        },
            reportError: errors.Add,
            confirmPendingCleanupExit: verifyExitDialog ? null : static () => true,
            windowsAutomationFactory: shared =>
            {
                cleanup = shared;
                return new WindowsAutomation(_ => throw new InvalidOperationException("入力送信は使用しません。"),
                    swappedMouseButtons: false, keyStateBackend: new Keys(),
                    selectiveInputFactory: (_, devices) =>
                    {
                        bool failRelease = hooks.Count == 0
                            || (verifyExitDialog && hooks.Count == 2);
                        var hook = new Hook(devices, failRelease ? 2 : 0);
                        hooks.Add(hook);
                        return hook;
                    })
                {
                    SelectiveInputCleanup = shared
                };
            })
        {
            ShowActivated = false
        };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Button play = FindButton("PlayButton");
            Button record = FindButton("RecordButton");
            Button retry = FindButton("InputCleanupButton");
            var options = new ScriptRunOptions([]);
            int failedCode = await window.RunCommandLineAsync($"LOCKHARDEX(0,{mode})", options);
            Require(failedCode == 1 && errors.Count == 1, "実行結果と診断の保持");
            Require(cleanup?.PendingCount == 1 && hooks.Count == 1
                && hooks[0].IsRegistered && hooks[0].Stops == 2,
                "公開スクリプトの終了後に未回収所有者を保持");
            Require(hooks[0].Devices == (mode == 0 ? SelectiveInputDevices.All : (SelectiveInputDevices)mode),
                "公開モードの登録");
            Require(!play.IsEnabled && !record.IsEnabled && retry.IsEnabled,
                "未回収中の開始制限");
            nint owner = new WindowInteropHelper(window).Handle;
            bool exitDialogDeclined = false;
            if (verifyExitDialog)
            {
                var observer = Task.Run(() =>
                    ConfirmationDialogVerification.ChooseAsync(owner,
                        MessageBoxResult.No, "未回収所有者: 1。",
                        MessageBoxResult.No));
                window.Close();
                await observer;
                Require(window.IsVisible && cleanup!.PendingCount == 1
                    && hooks[0].IsRegistered && hooks[0].Stops == 2
                    && !play.IsEnabled && !record.IsEnabled && retry.IsEnabled,
                    "終了確認でいいえを選ぶと未回収状態を保持して本体を残す");
                exitDialogDeclined = true;
            }

            int blockedCode = await window.RunCommandLineAsync("LOCKHARDEX(0)", options);
            Require(blockedCode == 2 && hooks.Count == 1, "未回収中の再実行を拒否");
            await File.WriteAllTextAsync(transferScript, "PRINT PARAM_STR[0]");
            var editor = (TextBox)window.FindName("Editor");
            editor.Text = "PRINT 123";
            var request = new InteractiveInstance.RunRequest(transferScript, ["回収後の実行"]);
            using var instance = new InteractiveInstance();
            using var sender = new InteractiveInstance();
            Require(instance.IsFirst && !sender.IsFirst, "単一起動の受付と送信を確保");
            Task listener = instance.ListenAsync(window.Dispatcher,
                window.OpenInteractiveLaunch, window.StartInteractiveLaunch);
            Require(!await sender.RequestOpenAsync(null, request)
                && editor.Text == "PRINT 123" && discards == 0,
                "未回収中は原稿を保持して単一起動の実行要求を拒否");
            hooks[0].FinishRecovery = finishRecovery;
            try
            {
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await hooks[0].RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!await sender.RequestOpenAsync(null, request)
                    && editor.Text == "PRINT 123" && discards == 0,
                    "再回収の途中も原稿を保持して実行要求を拒否");
            }
            finally
            {
                finishRecovery.Set();
            }

            await WaitAsync(() => play.IsEnabled && record.IsEnabled && !retry.IsEnabled);
            Require(cleanup!.PendingCount == 0 && !hooks[0].IsRegistered && hooks[0].Stops == 3,
                "明示再回収と件数0での開始復帰");
            Require(((TextBlock)window.FindName("InputCleanupLabel")).Text.Contains("回収完了")
                && ((TextBox)window.FindName("Output")).Text.Contains("公開回収の検証用解除失敗"),
                "回収完了と過去の診断を両方表示");
            int rerunCode = await window.RunCommandLineAsync($"LOCKHARDEX(0,{mode})", options);
            Require(rerunCode == 0 && hooks.Count == 2 && !hooks[1].IsRegistered
                && cleanup.PendingCount == 0 && errors.Count == 1,
                "回収後の新規実行と終了回収");
            Require(await sender.RequestOpenAsync(null, request),
                "回収後に単一起動の実行要求を受付");
            await WaitAsync(() => ((TextBox)window.FindName("Output")).Text
                .Contains("回収後の実行") && play.IsEnabled);
            Require(!listener.IsCompleted, "拒否と受付の応答後もパイプを維持");

            int pendingAfterRecovery = cleanup.PendingCount;
            bool exitDialogAccepted = false;
            if (verifyExitDialog)
            {
                int closeCode = await window.RunCommandLineAsync(
                    $"LOCKHARDEX(0,{mode})", options);
                Require(closeCode == 1 && hooks.Count == 3
                    && cleanup.PendingCount == 1,
                    "終了確認用に未回収所有者を再度残す");
                editor.Text = "PRINT 456";
                int priorDiscards = discards;
                var observer = Task.Run(() =>
                    ConfirmationDialogVerification.ChooseAsync(owner,
                        MessageBoxResult.Yes, "未回収所有者: 1。",
                        MessageBoxResult.No));
                window.Close();
                await observer;
                Require(closed && discards == priorDiscards + 1
                    && cleanup.PendingCount == 1 && hooks[2].IsRegistered
                    && hooks[2].Stops == 2,
                    "はいを選ぶと未保存確認を経て本体を閉じ、解除成功を記録しない");
                exitDialogAccepted = true;
            }

            checks.Add(new
            {
                mode,
                failed_code = failedCode,
                blocked_code = blockedCode,
                rerun_code = rerunCode,
                pending = pendingAfterRecovery,
                exit_dialog_declined = exitDialogDeclined,
                exit_dialog_accepted = exitDialogAccepted,
                initial_stops = hooks[0].Stops,
                rerun_stops = hooks[1].Stops,
                diagnostic_preserved = true,
                starts_restored = true,
                transferred_run_blocked_and_restored = true,
                named_pipe = true,
                transferred_run_blocked_during_recovery = true
            });
        }
        finally
        {
            File.Delete(transferScript);
            foreach (Hook hook in hooks)
            {
                hook.Failures = 0;
            }
            if (cleanup is not null)
            {
                await cleanup.RetryAsync();
            }
            if (!closed)
            {
                await window.RefreshInputCleanupAsync();
                window.Close();
            }
        }

        Button FindButton(string name) => (Button)window.FindName(name);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("公開入力回収UIの検証期限");
            }
            await Task.Delay(10);
        }
    }

    private sealed class Hook(SelectiveInputDevices devices, int failures) : ISelectiveInputHookSession
    {
        internal SelectiveInputDevices Devices => devices;
        internal int Failures = failures;
        internal int Stops;
        internal ManualResetEventSlim? FinishRecovery
        {
            get; set;
        }

        internal TaskCompletionSource RecoveryEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRegistered
        {
            get; private set;
        }
        public void Start() => IsRegistered = true;
        public void Stop()
        {
            Stops++;
            if (Failures-- > 0)
            {
                throw new Win32Exception(5, "公開回収の検証用解除失敗");
            }
            if (FinishRecovery is not null)
            {
                RecoveryEntered.TrySetResult();
                if (!FinishRecovery.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("再回収中の要求を確認できませんでした。");
                }
            }

            IsRegistered = false;
        }
        public void Suspend()
        {
        }
        public bool Pump() => false;
        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);
        public void RefreshTargets()
        {
        }
    }

    private sealed class Keys : IKeyStateBackend
    {
        public short ReadAsync(int key) => 0;
        public short ReadToggle(int key) => 0;
        public bool ReadIme(CancellationToken token) => false;
    }
}
