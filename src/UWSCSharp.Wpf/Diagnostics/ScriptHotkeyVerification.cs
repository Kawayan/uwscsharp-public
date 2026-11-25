using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal static class ScriptHotkeyVerification
{
    internal static async Task<int> RunAsync(string path, bool verifyPause = false)
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("ホットキーのGUI検証はWindows Sandbox内で実行してください。");
        }

        MainWindow? window = null;
        try
        {
            window = new MainWindow(commandLine: true) { ShowActivated = false };
            window.Show();
            if (verifyPause)
            {
                await DiagnosticInputTarget.PrepareAsync(window);
            }

            List<string> checks = [];
            foreach (int expected in new[] { 0, 7, 0 })
            {
                int exit = await window.RunCommandLineAsync($$"""
                    IF !SETHOTKEY(122,6,"Pressed") THEN EXITEXIT(99)
                    EXITEXIT({{expected}})
                    PROCEDURE Pressed()
                    FEND
                    """, new ScriptRunOptions([]));
                if (exit != expected)
                {
                    throw new InvalidOperationException($"再実行の終了コードが異なります: {exit}");
                }

                VerifyReleased(window);
                checks.Add($"same-window-exit-{expected}-registration-released");
            }

            checks.AddRange(await VerifyInterruptedRunsAsync(window));
            await VerifyApplicationConflictsAsync(window);
            checks.Add("application-hotkey-conflicts-preserve-owner-across-runs");
            await AsyncKeyboardGuardVerification.RunAsync(window);
            checks.Add("async-keyboard-guard-restores-and-serializes");
            if (verifyPause)
            {
                checks.AddRange(await ScriptHotkeyPauseVerification.RunAsync(window));
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks,
                window_shown = true,
                input_injection = verifyPause
            }));
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

    // 操作用の登録とスクリプト用の登録が競合しても、所有者を変えないことを確認します。
    private static async Task VerifyApplicationConflictsAsync(MainWindow window)
    {
        var access = new MainWindow.ScriptHotkeyVerificationAccess(window);
        var settings = new HotkeySettings();
        if (!access.ApplyHotkeys(settings))
        {
            throw new InvalidOperationException("検証用の操作ホットキーを登録できません。");
        }

        for (int run = 0; run < 2; run++)
        {
            foreach (int key in settings.Keys)
            {
                int exit = await window.RunCommandLineAsync($$"""
                    IF SETHOTKEY({{key}},{{settings.Modifiers}},"Pressed") THEN EXITEXIT(98)
                    IF !SETHOTKEY(122,6,"Pressed") THEN EXITEXIT(99)
                    EXITEXIT(0)
                    PROCEDURE Pressed()
                    FEND
                    """, new ScriptRunOptions([]));
                if (exit != 0)
                {
                    throw new InvalidOperationException($"操作ホットキーとの競合結果が異なります: {key}, {exit}");
                }

                VerifyReleased(window);
                await VerifyApplicationRegistrationsAsync(settings);
                if (!access.ActiveKeys.SequenceEqual(settings.Keys))
                {
                    throw new InvalidOperationException("操作ホットキーの登録状態が変化しました。");
                }
            }
        }
    }

    // 通常の実行経路を使い、停止と例外の後も同じ画面で再実行します。
    private static async Task<string[]> VerifyInterruptedRunsAsync(MainWindow window)
    {
        var access = new MainWindow.PlaybackVerificationAccess(window);
        const string registration = "IF !SETHOTKEY(122,6,\"Pressed\") THEN EXITEXIT(99)\n";
        const string procedure = "\nPROCEDURE Pressed()\nFEND";
        List<string> checks = [];
        Task<int> stopped = window.RunCommandLineAsync(
            registration + "PRINT \"hotkey-ready\"\nSLEEP(60)" + procedure,
            new ScriptRunOptions([]));
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!access.Output.Contains("hotkey-ready", StringComparison.Ordinal))
            {
                if (stopped.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new InvalidOperationException("停止対象のホットキー登録を確認できません。");
                }

                await Task.Delay(10);
            }
        }
        finally
        {
            access.RequestStop();
            int exit = await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            if (exit != 130)
            {
                throw new InvalidOperationException($"停止時の終了コードが異なります: {exit}");
            }
        }

        VerifyReleased(window);
        await VerifyRetryAsync();
        checks.Add("same-window-cancellation-release-and-retry");

        int failed = await window.RunCommandLineAsync(
            registration + "PRINT missing_hotkey_verification_variable" + procedure,
            new ScriptRunOptions([]));
        if (failed != 1)
        {
            throw new InvalidOperationException($"例外時の終了コードが異なります: {failed}");
        }

        VerifyReleased(window);
        await VerifyRetryAsync();
        checks.Add("same-window-error-release-and-retry");
        return [.. checks];

        async Task VerifyRetryAsync()
        {
            int exit = await window.RunCommandLineAsync(registration + "EXITEXIT(0)" + procedure,
                new ScriptRunOptions([]));
            if (exit != 0)
            {
                throw new InvalidOperationException($"再実行に失敗しました: {exit}");
            }

            VerifyReleased(window);
        }
    }

    internal static Task VerifyApplicationRegistrationsAsync(HotkeySettings settings)
    {
        // 別の所有スレッドで競合を確認し、誤って取得できた場合も検証用登録を残しません。
        return Task.Run(() =>
        {
            foreach (int key in settings.Keys)
            {
                if (!RegisterHotKey(0, 0x44ac, settings.Modifiers, key))
                {
                    continue;
                }

                if (!UnregisterHotKey(0, 0x44ac))
                {
                    throw new InvalidOperationException("競合確認用の登録を解除できません。");
                }

                throw new InvalidOperationException($"操作用ホットキーのOS登録が失われています: {key}");
            }
        });
    }

    internal static void VerifyReleased(MainWindow window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (!RegisterHotKey(handle, 0x44ab, 6, 122))
        {
            throw new InvalidOperationException("実行終了後もスクリプトの登録が残っています。");
        }

        if (!UnregisterHotKey(handle, 0x44ab))
        {
            throw new InvalidOperationException("検証用登録を解除できません。");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, int key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
