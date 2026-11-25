using System.IO;
using System.Text.Json;
using System.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class TrayVerification
{
    internal static Task<int> SaveExitStateAsync(string receipt)
    {
        Require(SandboxEnvironment.IsCurrent, "Sandbox 内の検証");
        string state = ApplicationInfo.SettingsPath("start-in-tray");
        Require(!File.Exists(state), "既存の起動状態がない");
        var window = new MainWindow(false, reportError: error =>
            throw new InvalidOperationException("次回起動状態を保存できません。", error));
        var tray = new MainWindow.TrayVerificationAccess(window);
        try
        {
            window.ShowAtStartup(startInTray: true, startupStatePath: state);
            Require(!window.IsVisible && tray.Icon?.Visible == true, "終了前のトレイ格納");
            tray.Exit();
            Require(tray.Icon is null && File.Exists(state), "トレイ終了と次回状態の保存");
            File.WriteAllText(receipt, "Passed");
            return Task.FromResult(0);
        }
        finally
        {
            window.Close();
        }
    }

    internal static async Task<int> RunAsync(string receiptPath)
    {
        var checks = new List<string>();
        string? error = null;
        bool allowDiscard = false;
        var failures = new List<Exception>();
        var window = new MainWindow(false,
            confirmDiscard: () => allowDiscard,
            reportError: failures.Add);
        var tray = new MainWindow.TrayVerificationAccess(window);
        string state = receiptPath + ".tray-state";
        try
        {
            window.ShowAtStartup(startInTray: true, startupStatePath: state);
            nint originalHandle = tray.Handle;
            Require(!window.IsVisible && tray.Icon?.Visible == true
                && originalHandle != 0 && failures.Count == 0,
                "起動時の格納とハンドル初期化");
            tray.Restore();
            Require(window.IsVisible && tray.Handle == originalHandle
                && tray.Icon?.Visible == false, "起動時に格納した画面の復帰");
            checks.Add("startup-in-tray-and-restore");
            tray.SetSource("PRINT 123");
            tray.ClickTrayButton();
            Require(!window.IsVisible && tray.Icon?.Visible == true
                && failures.Count == 0, "格納とアイコン登録");
            checks.Add("hide-with-notification-icon");

            tray.Restore();
            Require(window.IsVisible && tray.Icon?.Visible == false
                && tray.Source == "PRINT 123" && tray.IsModified
                && tray.Handle == originalHandle,
                "復帰時の編集内容とハンドル維持");
            checks.Add("restore-preserves-unsaved-editor-and-handle");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                tray.Hide();
                tray.Restore();
            }

            Require(window.IsVisible && tray.Icon?.Visible == false,
                "繰返し格納と復帰");
            checks.Add("repeated-hide-and-restore");

            window.WindowState = WindowState.Minimized;
            tray.Hide();
            tray.Restore();
            Require(window.WindowState == WindowState.Normal
                && window.IsVisible, "最小化からの復帰");
            checks.Add("restore-minimized-window");

            var playback = new MainWindow.RecordingVerificationAccess(window);
            tray.SetSource("PRINT 123");
            tray.Hide();
            Require(await playback.PlayAsync() == 0
                && !window.IsVisible && tray.Icon?.Visible == true
                && failures.Count == 0, "格納中の再生完了");
            checks.Add("playback-completes-in-tray");

            tray.SetSource("SLEEP(30)");
            Task<int> running = playback.PlayAsync();
            try
            {
                Require(!running.IsCompleted && !window.IsVisible
                    && tray.Icon?.Visible == true, "格納中の再生");
                playback.ClickStop();
                Require(await running.WaitAsync(TimeSpan.FromSeconds(10)) == 130
                    && !window.IsVisible && tray.Icon?.Visible == true
                    && failures.Count == 0, "格納を維持した再生停止");
                checks.Add("stop-playback-in-tray");
            }
            finally
            {
                if (!running.IsCompleted)
                {
                    playback.ClickStop();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            tray.Exit();
            Require(window.IsVisible && tray.Icon?.Visible == false
                && tray.IsModified && !File.Exists(state), "未保存確認で終了取消");
            checks.Add("cancel-exit-restores-window");

            tray.Hide();
            allowDiscard = true;
            tray.Exit();
            Require(!window.IsVisible && tray.Icon is null,
                "終了時のアイコン解放");
            checks.Add("exit-disposes-notification-icon");
            Require(File.Exists(state), "トレイ終了時に次回の状態を保存");
            var next = new MainWindow(false, reportError: failures.Add);
            var nextTray = new MainWindow.TrayVerificationAccess(next);
            try
            {
                next.ShowAtStartup(startInTray: false, startupStatePath: state,
                    restoreStartupState: true);
                Require(!next.IsVisible && nextTray.Icon?.Visible == true,
                    "次回は保存した状態からトレイに格納");
                nextTray.Restore();
            }
            finally
            {
                next.Close();
            }

            Require(!File.Exists(state) && failures.Count == 0,
                "通常の終了後は次回のトレイ起動を解除");
            checks.Add("tray-exit-persists-until-normal-close");
        }
        catch (Exception failure)
        {
            error = failure.ToString();
        }
        finally
        {
            allowDiscard = true;
            window.Close();
            File.Delete(state);
        }

        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            result = error is null ? "PASS" : "FAIL",
            checks,
            error,
            window_shown = true,
            input_injection = false
        }));
        return error is null ? 0 : 1;
    }
}
