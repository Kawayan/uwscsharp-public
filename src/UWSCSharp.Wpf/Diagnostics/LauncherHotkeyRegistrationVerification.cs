using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class LauncherHotkeyRegistrationVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("この検証はSandbox内だけで実行します。");
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "launcher.json");
        var shortcut = new ScriptHotkey(130, 6);
        LauncherSettingsStore.Save(path, new LauncherSettings(shortcut: shortcut));
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, launcherSettingsPath: path,
            verifyRecordingInput: true);
        var access = new MainWindow.LauncherVerificationAccess(main);
        try
        {
            main.Show();
            VerificationAssert.Require(access.ActiveHotkey == shortcut,
                "保存したランチ用ホットキーを登録する");
            VerifyFailedChanges(access, directory, path, shortcut);
            await VerifyRecordingAsync(directory, access);
            VerificationAssert.Require(errors.Count == 0, "画面操作のエラーなし");
            File.WriteAllText(receipt + ".ready", "");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(receipt + ".close"))
            {
                await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            main.Close();
        }

        VerificationAssert.Require(access.IsClosed && access.ActiveHotkey is null
            && access.PendingHotkeys == 0, "画面を閉じると登録を回収する");
        File.WriteAllText(receipt, "Passed");
        return 0;
    }

    private static async Task VerifyRecordingAsync(string directory,
        MainWindow.LauncherVerificationAccess access)
    {
        int pid = int.Parse(File.ReadAllText(Path.Combine(directory, "target.pid")),
            CultureInfo.InvariantCulture);
        using var target = Process.GetProcessById(pid);
        nint handle = target.MainWindowHandle;
        VerificationAssert.Require(pid != Environment.ProcessId && handle != 0,
            "入力先は別の検証用プロセスの画面である");
        access.DisableRecordingCopy();
        access.StartRecording();
        ushort[] shortcut = [162, 160, 130];
        try
        {
            await WaitForAsync(() => access.IsRecording && !access.IsStartingRecording,
                "記録を開始する");
            Native.SetForegroundWindow(handle);
            await DiagnosticInputTarget.PrepareAsync(handle, (uint)pid);
            VerificationAssert.Require(Native.GetForegroundWindow() == handle,
                "入力直前も検証画面が前面にある");
            SendKeys([Key(134, 0), Key(134, 2)]);
            SendKeys([.. shortcut.Select(key => Key(key, 0)),
                .. shortcut.Reverse().Select(key => Key(key, 2))]);
            await WaitForAsync(() => access.Menu?.IsOpen == true,
                "Windows の通知で記録中にランチを表示する");
            VerificationAssert.Require(access.IsRecording, "メニュー表示後も記録を継続する");
            access.Menu!.IsOpen = false;
            await access.FinishRecordingAsync();
            VerificationAssert.Require(access.Source.Contains("KBD(134,1)")
                && access.Source.Contains("KBD(134,2)"), "通常キーの押下と解放を記録する");
            foreach (int key in new[] { 16, 17, 130, 160, 162 })
            {
                VerificationAssert.Require(!access.Source.Contains($"KBD({key},"),
                    $"ランチ操作のキー {key} を記録しない");
            }
        }
        finally
        {
            await access.FinishRecordingAsync();
        }

        static async Task WaitForAsync(Func<bool> condition, string message)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition() && !deadline.IsCancellationRequested)
            {
                await Task.Delay(25);
            }

            VerificationAssert.Require(condition(), message);
        }

        static void SendKeys(Native.Input[] inputs)
        {
            int size = Marshal.SizeOf<Native.Input>();
            if (Native.SendInput((uint)inputs.Length, inputs, size) != inputs.Length)
            {
                Native.Input[] releases = [.. inputs.Where(input =>
                    input.Data.Keyboard.Flags == 2)];
                Native.SendInput((uint)releases.Length, releases, size);
                throw new InvalidOperationException("ゲストに全キーを送信できませんでした。");
            }
        }

        static Native.Input Key(ushort value, uint flags) => new()
        {
            Type = 1,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.KeyboardInput { Vk = value, Flags = flags }
            }
        };
    }

    private static void VerifyFailedChanges(MainWindow.LauncherVerificationAccess access,
        string directory, string path, ScriptHotkey original)
    {
        string mainPath = Path.Combine(directory, "hotkeys.json");
        var mainKeys = new HotkeySettings();
        access.SaveMainHotkeySettings(mainKeys, mainPath);
        VerificationAssert.Require(File.Exists(mainPath), "本体キーを登録する");
        byte[] saved = File.ReadAllBytes(path);
        var conflicting = new ScriptHotkey(mainKeys.RecordKey, (int)mainKeys.Modifiers);
        try
        {
            access.ApplyHotkeySettings(new LauncherSettings(shortcut: conflicting));
            throw new InvalidDataException("本体キーとの競合を拒否しませんでした。");
        }
        catch (InvalidDataException error) when (error.Message.Contains("本体の操作キー"))
        {
            VerificationAssert.Require(access.ActiveHotkey == original,
                "本体キーとの競合後も旧キーを維持する");
        }

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                access.ApplyHotkeySettings(new LauncherSettings(shortcut: new(131, 6)));
                throw new InvalidDataException("ロック中の設定を書き換えました。");
            }
            catch (IOException)
            {
                VerificationAssert.Require(access.ActiveHotkey == original
                    && access.PendingHotkeys == 0,
                    "保存失敗後に旧キーを維持し、新キーを回収する");
            }
        }

        VerificationAssert.Require(access.Settings?.Shortcut == original
            && saved.AsSpan().SequenceEqual(File.ReadAllBytes(path)),
            "保存失敗後も設定ファイルと画面の設定を維持する");
    }
}
