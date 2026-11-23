using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Core;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class StopOptionsVerification
{
    // 同じWPF画面で取消・再実行を行い、設定が次の実行へ残らないか検査します。
    private static async Task<string[]> VerifyStopOptionsAsync(MainWindow owner)
    {
        var access = new MainWindow.PlaybackVerificationAccess(owner);
        List<string> checks = [];
        var settings = new HotkeySettings();
        Require(access.ApplyHotkeys(settings), "検証用ホットキーを登録できません。");
        var options = new ScriptRunOptions([]);
        Require(await owner.RunCommandLineAsync("PRINT 1", options) == 0, "準備実行に失敗しました。");
        StopWindow window = access.StopWindow!;
        window.SetTopMost(false);
        Task<int> first = owner.RunCommandLineAsync("OPTION TOPSTOPFORM,NOSTOPHOTKEY\nSLEEP(30)", options);
        await WaitAsync(() => access.StopOptionsApplied);
        IScriptStopFormOptionsSession stale = access.ActiveStopOptions!;
        Require(window.IsTopMost(), "最前面固定が反映されていません。");
        Require(!access.IsHotkeyRegistered(HotkeyRegistration.StopHotkeyId), "停止キーが残っています。");
        Require(access.IsHotkeyRegistered(1) && access.IsHotkeyRegistered(2) && access.IsHotkeyRegistered(4),
            "停止以外の登録が解除されました。");
        Require(ProbeStopKey(), "解除した停止キーを別の登録で使用できません。");
        Require(StopOptionsPostMessage(new WindowInteropHelper(owner).Handle,
            0x312, HotkeyRegistration.StopHotkeyId, 0), "無効中の停止メッセージを送れません。");
        await Task.Delay(50);
        Require(!first.IsCompleted, "解除済み登録の停止メッセージが実行へ配送されました。");
        checks.Add("topmost-and-only-stop-key-disabled");
        window.Close();
        Require(await first.WaitAsync(TimeSpan.FromSeconds(10)) == 130, "閉じる操作で停止できません。");
        Require(!window.IsTopMost() && access.IsHotkeyRegistered(HotkeyRegistration.StopHotkeyId),
            "取消後に設定が復元されません。");
        Require(!ProbeStopKey(), "復元した停止キーがOSへ登録されていません。");
        checks.Add("close-still-cancels-and-restores-native-registration");

        Task<int> second = owner.RunCommandLineAsync("SLEEP(30)", options);
        await WaitAsync(() => access.IsPlaying);
        bool rejected = false;
        try
        {
            await stale.ApplyAsync(ScriptStopFormOption.TopMost, CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            rejected = true;
        }

        Require(rejected && !window.IsTopMost(), "終了済みセッションが次の実行へ反映されました。");
        // OS登録の確認とは分け、所有する画面への停止メッセージ配送を検査します。
        Require(StopOptionsPostMessage(new WindowInteropHelper(owner).Handle,
            0x312, HotkeyRegistration.StopHotkeyId, 0), "停止メッセージを送れません。");
        Require(await second.WaitAsync(TimeSpan.FromSeconds(10)) == 130, "再実行を停止できません。");
        checks.Add("same-window-rerun-rejects-stale-session-and-accepts-stop-message");

        Require(await owner.RunCommandLineAsync("OPTION TOPSTOPFORM,NOSTOPHOTKEY\nPRINT 1", options) == 0,
            "正常終了の検証に失敗しました。");
        Require(!window.IsTopMost() && access.IsHotkeyRegistered(HotkeyRegistration.StopHotkeyId),
            "正常終了後に設定が復元されません。");
        checks.Add("normal-completion-restores-only-changed-options");
        return [.. checks];

        bool ProbeStopKey()
        {
            nint handle = new WindowInteropHelper(owner).Handle;
            bool registered = StopOptionsRegisterHotKey(handle, 0x44aa,
                settings.Modifiers | 0x4000, settings.StopKey);
            if (registered)
            {
                Require(StopOptionsUnregisterHotKey(handle, 0x44aa), "検証用登録を解除できません。");
            }

            return registered;
        }

        static async Task WaitAsync(Func<bool> ready)
        {
            var timeout = Stopwatch.StartNew();
            while (!ready())
            {
                Require(timeout.Elapsed < TimeSpan.FromSeconds(10), "停止設定の適用が完了しません。");
                await Task.Delay(10);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")]
    private static extern bool StopOptionsRegisterHotKey(nint window, int id, uint modifiers, int key);
    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    private static extern bool StopOptionsUnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool StopOptionsPostMessage(nint window, uint message, nint wParam, nint lParam);

    internal static async Task<int> RunAsync(string path)
    {
        if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("停止設定のGUI検証はWindows Sandbox内で実行してください。");
        }

        MainWindow? window = null;
        try
        {
            window = new MainWindow(commandLine: true) { ShowActivated = false };
            window.Show();
            string[] checks = await VerifyStopOptionsAsync(window);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks,
                window_shown = true,
                input_injection = false,
                stop_message_delivery = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                error = error.ToString()
            }));
            return 1;
        }
        finally
        {
            window?.Close();
        }
    }
}
