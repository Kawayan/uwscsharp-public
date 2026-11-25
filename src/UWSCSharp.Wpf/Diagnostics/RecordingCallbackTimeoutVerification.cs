using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static partial class RecordingOsVerification
{
    // 入力を送る検査は、環境変数による自己申告だけでは許可しません。
    internal static async Task<int> RunTimeoutAsync(string receiptPath)
    {
        Require(SandboxEnvironment.IsCurrent
            && Process.GetCurrentProcess().SessionId > 0
            && File.Exists(@"C:\ui-input\initialize_ui_isolation_sandbox.ps1"),
            "callback期限検査はWindows Sandbox専用です。");
        var results = new List<object>();
        var checks = new List<string>();
        foreach (bool pendingStop in new[] { false, true })
        {
            string name = pendingStop ? "callback-timeout-stop-retry" : "callback-timeout-warning-and-restart";
            using var context = new Context { InjectInput = true };
            context.Hooks.DelayCallback = true;
            string? error = null;
            try
            {
                await CallbackTimeoutAsync(context, pendingStop);
                checks.Add(name);
            }
            catch (Exception failure)
            {
                error = failure.ToString();
            }

            results.Add(new
            {
                name,
                result = error is null ? "PASS" : "FAIL",
                error,
                observations = context.Observations,
                native_calls = context.Calls.ToArray()
            });
        }

        bool success = checks.Count == 2;
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            result = success ? "PASS" : "FAIL",
            checks,
            cases = results,
            window_shown = true,
            input_injection = true,
            computer_use = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    private static async Task CallbackTimeoutAsync(Context context, bool pendingStop)
    {
        await using var screen = new Screen(context);
        nint window = new WindowInteropHelper(screen.Window).Handle;
        Require(screen.Window.Activate() && Native.GetForegroundWindow() == window,
            "Sandbox内の所有画面への入力先限定");
        screen.Editor.Focus();
        int delivered = 0;
        HwndSource.FromHwnd(window).AddHook((nint target, int message, nint key,
            nint data, ref bool handled) =>
        {
            if (message is 0x101 or 0x105 && key is >= 133 and <= 135)
            {
                delivered++;
            }
            return 0;
        });
        screen.Record();
        await screen.WaitRecordingAsync();
        try
        {
            await SendKeyAsync(134);
            context.Observations["seed_callback_count"] = context.Hooks.KeyCount(134);
            context.Observations["seed_delivered"] = delivered;
            await WaitAsync(() => delivered >= 1);
            Require(Native.GetForegroundWindow() == window, "記録開始後の入力先保持");
            await WaitAsync(() => context.Hooks.KeyCount(134) >= 2);
            int pulses = screen.Pulses;
            Task trigger = SendKeyAsync(135);
            await context.Hooks.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var elapsed = Stopwatch.StartNew();
            if (pendingStop)
            {
                screen.Stop();
                screen.Close();
                Require(!screen.Closed && screen.CloseCanceled, "callback待機中の終了保留");
                await WaitAsync(() => screen.Errors.Count == 1);
                Require(screen.Errors[0] is TimeoutException, "callback待機中の停止期限");
                screen.RequireRecovery();
                screen.RequireOriginal();
            }
            else
            {
                await Task.Delay(1600);
            }

            context.Observations["blocked_milliseconds"] = elapsed.ElapsedMilliseconds;
            context.Observations["dispatcher_pulses"] = screen.Pulses - pulses;
            Require(screen.Pulses - pulses >= 2, "callback遅延中のGUI応答");
            Require(!context.Calls.Any(call => call.Operation.Contains("unregister", StringComparison.Ordinal)),
                "OS解除の再現前に手動解除しない");
            context.Hooks.AllowCallback.Set();
            await context.Hooks.CallbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            if (!pendingStop)
            {
                // 単発の遅延で解除されない環境では、同じ所有フックで繰り返します。
                for (int attempt = 1; attempt < 12; attempt++)
                {
                    int completed = context.Hooks.CallbackCompletions;
                    await SendKeyAsync(135);
                    var deadline = Stopwatch.StartNew();
                    while (context.Hooks.CallbackCompletions == completed
                        && deadline.Elapsed < TimeSpan.FromSeconds(3))
                    {
                        await Task.Delay(10);
                    }
                    if (context.Hooks.CallbackCompletions == completed)
                    {
                        break;
                    }
                }
                int before = delivered;
                await SendKeyAsync(133);
                await WaitAsync(() => delivered > before);
                context.Observations["post_timeout_callback_count"] = context.Hooks.KeyCount(133);
            }

            context.Observations["delayed_callbacks"] = context.Hooks.CallbackCompletions;
            screen.Stop();
            await WaitAsync(() => !screen.Controller.HasRecorder);
            bool removed = context.Calls.Any(call => !call.Success && call.Error == 1404);
            context.Observations["os_silent_removal_detected"] = removed;
            context.Observations["reproduction"] = removed ? "reproduced" : "not_reproduced";
            Require(screen.Errors.Count == (pendingStop ? 1 : 0), "想定外のGUIエラーなし");
            Require(screen.Editor.Text.Contains("KBD(134,1)", StringComparison.Ordinal)
                && screen.Editor.Text.Contains("KBD(134,2)", StringComparison.Ordinal),
                "期限超過前の記録結果を保持");
            Require(screen.Editor.Text.Contains("// 警告:", StringComparison.Ordinal) == removed,
                "OS解除の実測結果と記録警告の一致");
            if (removed)
            {
                await WaitAsync(() => screen.Status.Contains("解除済み", StringComparison.Ordinal)
                    && screen.Output.Contains("解除済み", StringComparison.Ordinal));
            }
            screen.RequireIdle();
            context.Observations["generated_source"] = screen.Editor.Text;
            context.Observations["gui_status"] = screen.Status;
            context.Observations["gui_output"] = screen.Output;
            context.Observations["gui_errors"] = screen.Errors.Select(item => item.ToString()).ToArray();
            context.Hooks.DelayCallback = false;
            int count = context.Hooks.KeyCount(133);
            screen.Record();
            await screen.WaitRecordingAsync();
            await SendKeyAsync(133);
            await WaitAsync(() => context.Hooks.KeyCount(133) >= count + 2);
            screen.Stop();
            await WaitAsync(() => !screen.Controller.HasRecorder);
            Require(screen.Editor.Text.Contains("KBD(133,1)", StringComparison.Ordinal), "再記録の実入力取得");
            screen.RequireIdle();
            context.Observations["restart_source"] = screen.Editor.Text;
        }
        finally
        {
            context.Hooks.AllowCallback.Set();
            // DOWNだけが処理された場合も、検証用キーを押下状態で残しません。
            SendInput(3, [new TestInput(133, 2), new TestInput(134, 2), new TestInput(135, 2)], 40);
        }
    }

    private static Task SendKeyAsync(ushort key) => Task.Run(() =>
    {
        // SendInputの一括挿入とは別に、旧APIの注入経路でも期限動作を観測します。
        keybd_event((byte)key, 0, 0, 0);
        keybd_event((byte)key, 0, 2, 0);
    });

    private sealed partial class NativeHooks
    {
        private readonly List<BasicRecorder.HookCallback> callbackRoots = [];
        private readonly ConcurrentDictionary<int, int> callbackCounts = new();
        private int callbackDelayed;
        private int callbackCompletions;

        internal int CallbackCompletions => Volatile.Read(ref callbackCompletions);

        internal bool DelayCallback
        {
            get; set;
        }

        internal ManualResetEventSlim AllowCallback { get; } = new();

        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CallbackReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int KeyCount(int key) => callbackCounts.GetValueOrDefault(key);

        private BasicRecorder.HookCallback WrapCallback(int type, BasicRecorder.HookCallback callback)
        {
            if (type != 13)
            {
                return callback;
            }

            BasicRecorder.HookCallback wrapper = (code, message, data) =>
            {
                if (code < 0)
                {
                    return callback(code, message, data);
                }

                int key = Marshal.ReadInt32(data);
                callbackCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
                if (!DelayCallback || key != 135 || message != 0x100)
                {
                    return callback(code, message, data);
                }

                // OSの待機期限後にも参照できるよう、callback引数を先に複製します。
                byte[] bytes = new byte[24];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                nint copy = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, copy, bytes.Length);
                try
                {
                    if (Interlocked.Increment(ref callbackDelayed) == 1)
                    {
                        CallbackEntered.TrySetResult();
                        AllowCallback.Wait(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        Thread.Sleep(1600);
                    }
                    return callback(code, message, copy);
                }
                finally
                {
                    Marshal.FreeHGlobal(copy);
                    Interlocked.Increment(ref callbackCompletions);
                    CallbackReturned.TrySetResult();
                }
            };
            callbackRoots.Add(wrapper);
            return wrapper;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct TestInput
    {
        [FieldOffset(0)] private uint type;
        [FieldOffset(8)] private ushort key;
        [FieldOffset(12)] private uint flags;

        internal TestInput(ushort key, uint flags)
        {
            this = default;
            type = 1;
            this.key = key;
            this.flags = flags;
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, TestInput[] inputs, int size);
}
