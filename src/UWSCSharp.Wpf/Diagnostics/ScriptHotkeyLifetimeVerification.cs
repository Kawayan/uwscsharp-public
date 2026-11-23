using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class ScriptHotkeyLifetimeVerification
{
    // 同じ表示画面から実行し、修飾子フックの解除と次の通知まで確認します。
    internal static async Task<int> RunAsync(string receipt)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("ホットキーの寿命検証はSandbox専用です。");
        }

        var platforms = new ConcurrentQueue<LifetimePlatform>();
        var sources = new ConcurrentQueue<WindowsScriptHotkeySource>();
        var errors = new List<Exception>();
        var checks = new List<object>();
        string? failure = null;
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-hotkey-lifetime-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var window = new MainWindow(false, confirmDiscard: () => true, reportError: errors.Add,
            windowsAutomationFactory: cleanup => new WindowsAutomation(
                _ => throw new InvalidOperationException("スクリプトからの入力送信は使用しません。"),
                swappedMouseButtons: false)
            {
                SelectiveInputCleanup = cleanup,
                ScriptHotkeySourceFactory = () =>
                {
                    var platform = new LifetimePlatform();
                    var source = new WindowsScriptHotkeySource(platform);
                    platforms.Enqueue(platform);
                    sources.Enqueue(source);
                    return source;
                }
            })
        {
            ShowActivated = false
        };
        var playback = new MainWindow.PlaybackVerificationAccess(window);
        var scheduler = new MainWindow.SchedulerVerificationAccess(window);
        long handle = 0;
        try
        {
            window.EditorPanel.IsExpanded = true;
            window.Show();
            await DiagnosticInputTarget.PrepareAsync(window);
            handle = new WindowInteropHelper(window).Handle.ToInt64();
            Require(Native.GetForegroundWindow() == (nint)handle,
                "ゲスト内のWPF前面化");
            string[] endings = ["normal", "retry", "error", "retry", "cancel", "retry"];
            int generation = 0;
            foreach (bool modifierOnly in new[] { false, true })
            {
                foreach (string ending in endings)
                {
                    generation++;
                    string finish = Path.Combine(directory, "finish-" + generation);
                    int beforeErrors = errors.Count;
                    int key = modifierOnly ? 0 : 122;
                    string text = $$"""
                        LOGPRINT(FALSE)
                        IF !SETHOTKEY({{key}},6,"Pressed") THEN EXITEXIT(99)
                        PRINT "ready-{{generation}}"
                        WHILE !FOPEN("{{finish}}",F_EXISTS)
                            SLEEP(0.01)
                        WEND
                        {{(ending == "error" ? "PRINT missing_hotkey_lifetime_variable" : "")}}
                        PROCEDURE Pressed()
                            PRINT "pressed-{{generation}}"
                        FEND
                        """;
                    Task<int> running = window.RunCommandLineAsync(text, new ScriptRunOptions([]));
                    await WaitAsync(running, () => playback.Output.Contains(
                        "ready-" + generation, StringComparison.Ordinal));
                    Require(UWSCSharp.Windows.Native.GetForegroundWindow() == (nint)handle,
                        "通知先の前面画面");
                    LifetimePlatform platform = platforms.ToArray()[generation - 1];
                    Require(!platform.Backend.IsPressed(0xA2) && !platform.Backend.IsPressed(0xA0),
                        "注入前のCtrlとShiftの解放");
                    ushort[] keys = modifierOnly ? [0xA2, 0xA0] : [0xA2, 0xA0, 122];
                    UWSCSharp.Windows.Native.Input[] inputs =
                    [
                        .. keys.Select(value => CreateLifetimeKeyInput(value, 0)),
                        .. keys.Reverse().Select(value => CreateLifetimeKeyInput(value, 2))
                    ];
                    Require(UWSCSharp.Windows.Native.SendInput((uint)inputs.Length,
                        inputs, Marshal.SizeOf<UWSCSharp.Windows.Native.Input>()) == inputs.Length,
                        "ゲスト内のホットキー注入");
                    await WaitAsync(running, () => playback.Output.Contains(
                        "pressed-" + generation, StringComparison.Ordinal));
                    if (ending == "cancel")
                    {
                        playback.RequestStop();
                    }
                    else
                    {
                        await File.WriteAllTextAsync(finish, "finish");
                    }

                    int exit = await running.WaitAsync(TimeSpan.FromSeconds(10));
                    WindowsScriptHotkeySource owned = sources.ToArray()[generation - 1];
                    var endingWatch = Stopwatch.StartNew();
                    while (owned.IsOwnerThreadAlive && endingWatch.Elapsed < TimeSpan.FromSeconds(3))
                    {
                        await Task.Delay(10);
                    }
                    Require(!owned.IsOwnerThreadAlive && owned.Failure is null,
                        "通知元の所有スレッドの終了");
                    Require(exit == (ending == "cancel" ? 130 : ending == "error" ? 1 : 0),
                        "終了結果: " + ending + ", " + exit);
                    Require(errors.Count == beforeErrors + (ending == "error" ? 1 : 0),
                        "エラー通知の件数");
                    Require(playback.Output.Split("pressed-", StringSplitOptions.None).Length == 2,
                        "前の実行の通知を混在させず1回だけ配送");
                    Require(platform.Registered == 1 && platform.Unregistered == 1
                        && platform.Shutdowns == 1 && !platform.Native.IsModifierCallbackRooted,
                        "OS登録とcallback参照の解放");
                    Require(platform.Backend.Installed == (modifierOnly ? 1 : 0)
                        && platform.Backend.Removed == platform.Backend.Installed,
                        "実OS修飾子フックの解除");
                    if (modifierOnly)
                    {
                        Require(platform.Backend.InstallThread == platform.Backend.RemoveThread
                            && platform.Backend.InstallThread != 0, "同じ所有スレッドでの解除");
                    }
                    Require(window.PlayButton.IsEnabled && window.RecordButton.IsEnabled
                        && !window.StopButton.IsEnabled && !window.Editor.IsReadOnly
                        && window.IsVisible && new WindowInteropHelper(window).Handle.ToInt64() == handle,
                        "同じ編集画面での開始状態の復帰"
                        + $" play={window.PlayButton.IsEnabled} record={window.RecordButton.IsEnabled}"
                        + $" stop={window.StopButton.IsEnabled} readonly={window.Editor.IsReadOnly}"
                        + $" visible={window.IsVisible} cleanup={playback.CanStartInput}"
                        + $" admission={scheduler.CanEnterInteractive}"
                        + $" sameHandle={new WindowInteropHelper(window).Handle.ToInt64() == handle}");
                    checks.Add(new
                    {
                        generation,
                        modifier_only = modifierOnly,
                        ending,
                        exit,
                        installed = platform.Backend.Installed,
                        removed = platform.Backend.Removed,
                        registered = platform.Registered,
                        unregistered = platform.Unregistered,
                        shutdowns = platform.Shutdowns,
                        callback_rooted = platform.Native.IsModifierCallbackRooted,
                        install_thread = platform.Backend.InstallThread,
                        remove_thread = platform.Backend.RemoveThread,
                        handle,
                        callback_count = 1,
                        starts_restored = true,
                        source_thread_alive = owned.IsOwnerThreadAlive
                    });

                    async Task WaitAsync(Task<int> operation, Func<bool> condition)
                    {
                        var watch = Stopwatch.StartNew();
                        while (!condition())
                        {
                            if (operation.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(10))
                            {
                                throw new InvalidOperationException("ホットキーの準備・通知待ち: "
                                    + window.StatusLabel.Text + " / " + playback.Output);
                            }
                            await Task.Delay(10);
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            failure = error + "\n状態: " + window.StatusLabel.Text + "\n実行結果: " + playback.Output;
        }
        finally
        {
            // この検証がゲストへ送ったキーだけを解放し、通知元を回収します。
            UWSCSharp.Windows.Native.Input[] releases = [CreateLifetimeKeyInput(122, 2),
                CreateLifetimeKeyInput(0xA0, 2), CreateLifetimeKeyInput(0xA2, 2)];
            UWSCSharp.Windows.Native.SendInput((uint)releases.Length,
                releases, Marshal.SizeOf<UWSCSharp.Windows.Native.Input>());
            playback.RequestStop();
            foreach (WindowsScriptHotkeySource source in sources)
            {
                try
                {
                    await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error)
                {
                    failure = (failure ?? "") + "\n後始末: " + error;
                }
            }
            var watch = Stopwatch.StartNew();
            while (playback.IsPlaying && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
            }
            if (playback.IsPlaying)
            {
                failure = (failure ?? "") + "\n実行終了の待機期限";
            }
            window.Close();
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(directory).StartsWith(temp, StringComparison.OrdinalIgnoreCase),
                "今回作成した一時フォルダーの範囲");
            Directory.Delete(directory, recursive: true);
        }

        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = failure is null ? "PASS" : "FAIL",
            error = failure,
            checks,
            process_id = Environment.ProcessId,
            handle,
            window_shown = true,
            input_injection = true,
            limits = "ゲスト内の注入キーと表示したWPFの通常実行処理。物理入力・実OS障害・全競合・全環境の受入とは区別する。"
        }));
        return failure is null ? 0 : 1;
    }

    private static UWSCSharp.Windows.Native.Input CreateLifetimeKeyInput(ushort value, uint flags) => new()
    {
        Type = 1,
        Data = new UWSCSharp.Windows.Native.InputUnion
        {
            Keyboard = new UWSCSharp.Windows.Native.KeyboardInput { Vk = value, Flags = flags }
        }
    };

    private sealed class LifetimePlatform : IScriptHotkeyPlatform
    {
        internal readonly LifetimeBackend Backend = new();
        internal readonly NativeScriptHotkeyPlatform Native;
        internal int Registered;
        internal int Unregistered;
        internal int Shutdowns;

        internal LifetimePlatform() => Native = new NativeScriptHotkeyPlatform(Backend);

        public void Initialize() => Native.Initialize();

        public bool Register(int id, ScriptHotkey key)
        {
            bool result = Native.Register(id, key);
            if (result)
            {
                Registered++;
            }
            return result;
        }

        public bool Unregister(int id)
        {
            bool result = Native.Unregister(id);
            if (result)
            {
                Unregistered++;
            }
            return result;
        }

        public bool TryRead(out int id) => Native.TryRead(out id);

        public void Wait(AutoResetEvent work) => Native.Wait(work);

        public void Shutdown()
        {
            Native.Shutdown();
            Shutdowns++;
        }
    }

    private sealed class LifetimeBackend : IModifierHookBackend
    {
        private readonly NativeModifierHookBackend native = new();
        internal int Installed;
        internal int Removed;
        internal int InstallThread;
        internal int RemoveThread;

        public nint Install(ModifierHookProcedure procedure)
        {
            nint handle = native.Install(procedure);
            if (handle != 0)
            {
                Installed++;
                InstallThread = Environment.CurrentManagedThreadId;
            }
            return handle;
        }

        public bool Remove(nint hook)
        {
            bool result = native.Remove(hook);
            if (result)
            {
                Removed++;
                RemoveThread = Environment.CurrentManagedThreadId;
            }
            return result;
        }

        public bool IsPressed(int key) => native.IsPressed(key);

        public nint Next(int code, nuint message, nint data) => native.Next(code, message, data);
    }
}
