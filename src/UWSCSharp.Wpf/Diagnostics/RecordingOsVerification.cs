using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// 所有するフックとRaw Input登録だけを操作し、実OSの返値とGUI復旧を記録します。
internal static partial class RecordingOsVerification
{
    internal static async Task<int> RunAsync(string receiptPath)
    {
        var results = new List<object>();
        var checks = new List<string>();
        (string Name, Func<Context, Task> Run)[] cases =
        [
            ("native-partial-registration-failure-and-restart", PartialRegistrationAsync),
            ("native-raw-input-registration-loss-and-gui-recovery", RawInputLossAsync),
            ("native-release-delay-and-close-retry", ReleaseDelayAsync),
            ("native-already-removed-hook-preserves-recording-with-warning", RemovedHookAsync)
        ];
        foreach ((string name, Func<Context, Task> run) in cases)
        {
            using var context = new Context();
            string? error = null;
            try
            {
                await run(context);
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

        bool success = checks.Count == cases.Length;
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            result = success ? "PASS" : "FAIL",
            checks,
            cases = results,
            window_shown = true,
            input_injection = false,
            computer_use = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    private static async Task PartialRegistrationAsync(Context context)
    {
        context.Hooks.FailMouseRegistration = true;
        await using var screen = new Screen(context);
        screen.Record();
        await WaitAsync(() => screen.Errors.Count == 1);
        Require(screen.Errors[0].GetBaseException() is Win32Exception, "実APIの登録失敗");
        context.Observations["registration_error"] = screen.Errors[0].ToString();
        Require(context.Calls.Any(call => call.Operation == "unregister" && call.Success),
            "登録済みフックの解放");
        screen.RequireIdle();
        screen.RequireOriginal();
        context.Hooks.FailMouseRegistration = false;
        screen.Record();
        await screen.WaitRecordingAsync();
        screen.Close();
        await WaitAsync(() => screen.Closed);
        Require(context.Hooks.ActiveCount == 0, "再開始後のフック解放");
    }

    private static async Task RawInputLossAsync(Context context)
    {
        await using var screen = new Screen(context);
        screen.Record();
        await screen.WaitRecordingAsync();
        RawDevice[] devices = Registrations();
        Require(devices.Length == 2, "記録プロセスのRaw Input登録");
        foreach (RawDevice device in devices)
        {
            Native.GetWindowThreadProcessId(device.Target, out uint process);
            Require(process == Environment.ProcessId, "Raw Input対象の所有権");
        }

        context.Observations["raw_input_before_removal"] = devices.Length;
        RawDevice[] remove = [.. devices.Select(device => new RawDevice
        {
            Page = device.Page, Usage = device.Usage, Flags = 1
        })];
        Require(RegisterRawInputDevices(remove, (uint)remove.Length,
            (uint)Marshal.SizeOf<RawDevice>()), "実APIによるRaw Input登録解除");
        Require(Registrations().Length == 0, "登録解除のOS側確認");
        await WaitAsync(() => screen.Errors.Count == 1);
        context.Observations["monitor_error"] = screen.Errors[0].ToString();
        Require(screen.Errors[0].ToString().Contains("監視登録が変更", StringComparison.Ordinal),
            "定期監視による登録変更の検出");
        screen.RequireIdle();
        screen.RequireOriginal();
        Require(context.Hooks.ActiveCount == 0, "登録喪失後のフック解放");
        screen.Record();
        await screen.WaitRecordingAsync();
        Require(Registrations().Length == 2, "再開始後のRaw Input再登録");
        screen.Close();
        await WaitAsync(() => screen.Closed);
        Require(Registrations().Length == 0, "再開始後のRaw Input解放");
    }

    private static async Task ReleaseDelayAsync(Context context)
    {
        context.Hooks.DelayRelease = true;
        await using var screen = new Screen(context);
        screen.Record();
        await screen.WaitRecordingAsync();
        var elapsed = Stopwatch.StartNew();
        int pulses = screen.Pulses;
        screen.Stop();
        await context.Hooks.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        screen.Close();
        Require(!screen.Closed && screen.CloseCanceled, "停止待機中の終了保留");
        await WaitAsync(() => screen.Errors.Count == 1);
        Require(screen.Errors[0] is TimeoutException, "実workerの終了待機期限");
        context.Observations["stop_timeout_milliseconds"] = elapsed.ElapsedMilliseconds;
        context.Observations["dispatcher_pulses_while_stopping"] = screen.Pulses - pulses;
        Require(screen.Pulses - pulses >= 2, "停止遅延中のDispatcher応答");
        screen.RequireRecovery();
        screen.RequireOriginal();
        context.Hooks.AllowRelease.Set();
        screen.Close();
        await WaitAsync(() => screen.Closed);
        Require(context.Hooks.ActiveCount == 0 && Registrations().Length == 0,
            "遅延解消後のフックとRaw Input解放");
        Require(screen.Errors.Count == 1, "再試行時の追加失敗なし");
    }

    private static async Task RemovedHookAsync(Context context)
    {
        await using var screen = new Screen(context);
        screen.Record();
        await screen.WaitRecordingAsync();
        Require(context.Hooks.RemoveKeyboard(), "所有フックの事前解除");
        screen.Stop();
        await WaitAsync(() => !screen.Controller.HasRecorder);
        context.Observations["gui_errors"] = screen.Errors.Select(error => error.ToString()).ToArray();
        context.Observations["recording_warning"] = screen.Recorder!.RecordingWarning;
        context.Observations["generated_source"] = screen.Editor.Text;
        Require(context.Calls.Any(call => call.Operation == "unregister"
            && !call.Success && call.Error == 1404), "実OSの解除済みハンドル応答");
        Require(screen.Errors.Count == 0, "解除済みフックだけで記録結果を取得不能にしない");
        Require(screen.Recorder.RecordingWarning.Contains("解除済み", StringComparison.Ordinal),
            "解除済みフックによる欠落の警告");
        Require(screen.Editor.Text.Contains("// 警告:", StringComparison.Ordinal),
            "生成スクリプトへの警告反映");
        await WaitAsync(() => screen.Status.Contains("解除済み", StringComparison.Ordinal)
            && screen.Output.Contains("解除済み", StringComparison.Ordinal));
        context.Observations["gui_status"] = screen.Status;
        context.Observations["gui_output"] = screen.Output;
        screen.RequireIdle();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(15), "実OS検査の状態遷移期限");
            await Task.Delay(10);
        }
    }

    private sealed class Screen : IAsyncDisposable
    {
        private const string Original = "PRINT \"実OS検証前の未保存ソース\"";
        private readonly Context context;
        private readonly MainWindow window;
        private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromMilliseconds(50) };

        internal Screen(Context context)
        {
            this.context = context;
            Controller = new RecordingController(() =>
            {
                Recorder = context.InjectInput
                    ? new BasicRecorder(context.Hooks, includeInjected: true)
                    : new BasicRecorder(context.Hooks, new RawInputHookMonitor());
                return new BasicRecordingDevice(Recorder);
            }, TimeSpan.FromSeconds(3));
            window = new MainWindow(false, recordingController: Controller,
                confirmDiscard: () => true, reportError: Errors.Add)
            {
                ShowActivated = context.InjectInput,
                Title = "UWSC実OS記録検証-" + Guid.NewGuid()
            };
            window.Closing += (_, args) => CloseCanceled = args.Cancel;
            window.Closed += (_, _) => Closed = true;
            window.Show();
            nint handle = new WindowInteropHelper(window).Handle;
            Require(Native.IsWindowVisible(handle), "実WPF画面の表示");
            Editor.Text = Original;
            pulse.Tick += (_, _) => Pulses++;
            pulse.Start();
        }

        internal RecordingController Controller
        {
            get;
        }

        internal BasicRecorder? Recorder
        {
            get; private set;
        }

        internal List<Exception> Errors { get; } = [];

        internal bool Closed
        {
            get; private set;
        }

        internal bool CloseCanceled
        {
            get; private set;
        }

        internal int Pulses
        {
            get; private set;
        }

        internal MainWindow Window => window;

        internal TextBox Editor => (TextBox)window.FindName("Editor");

        internal string Status => ((TextBlock)window.FindName("StatusLabel")).Text;

        internal string Output => ((TextBox)window.FindName("Output")).Text;

        internal void Record() => ((Button)window.FindName("RecordButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void Stop() => ((Button)window.FindName("StopButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void Close() => window.Close();

        internal Task WaitRecordingAsync() => WaitAsync(() =>
            !Controller.IsStarting && Controller.IsRunning);

        internal void RequireOriginal() => Require(Editor.Text == Original,
            "実OS失敗時の未保存ソース保持");

        internal void RequireIdle() => Require(!Controller.HasRecorder
            && !Editor.IsReadOnly && ((Button)window.FindName("PlayButton")).IsEnabled
            && ((Button)window.FindName("RecordButton")).IsEnabled
            && !((Button)window.FindName("StopButton")).IsEnabled,
            "実OS失敗後の編集と再生への復帰");

        internal void RequireRecovery() => Require(Controller.RecoveryPending
            && Editor.IsReadOnly && ((Button)window.FindName("StopButton")).IsEnabled
            && ((Button)window.FindName("RecordButton")).IsEnabled
            && !((Button)window.FindName("PlayButton")).IsEnabled,
            "停止期限後の再試行への復帰");

        /// <summary>
        /// 検証の遅延を解除し、所有するworkerと画面を終了します。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            context.Hooks.AllowRelease.Set();
            context.Hooks.AllowCallback.Set();
            await WaitAsync(() => !Controller.IsStarting && !Controller.IsStopping);
            if (Controller.HasRecorder)
            {
                try
                {
                    await Controller.StopAsync();
                }
                catch (Exception failure)
                {
                    context.Observations["cleanup_error"] = failure.ToString();
                }
            }

            pulse.Stop();
            if (!Closed)
            {
                window.Close();
            }

            Require(context.Hooks.ActiveCount == 0, "検証終了時の所有フック解放");
        }
    }

    private sealed class Context : IDisposable
    {
        internal Context() => Hooks = new NativeHooks(Calls);

        internal bool InjectInput
        {
            get; set;
        }

        internal ConcurrentQueue<HookCall> Calls { get; } = new();

        internal Dictionary<string, object> Observations { get; } = new();

        internal NativeHooks Hooks
        {
            get;
        }

        /// <summary>
        /// 所有する検証用の待機資源を解放します。
        /// </summary>
        public void Dispose()
        {
            Hooks.AllowRelease.Dispose();
            Hooks.AllowCallback.Dispose();
        }
    }

    private sealed record HookCall(string Operation, int Type, long Handle,
        bool Success, int Error, long Time);

    private sealed partial class NativeHooks(ConcurrentQueue<HookCall> calls) : BasicRecorder.IRecordingHooks
    {
        private readonly BasicRecorder.NativeRecordingHooks native = new();
        private readonly ConcurrentDictionary<int, nint> handles = new();
        private readonly ConcurrentDictionary<nint, int> active = new();
        private int delayed;
        [ThreadStatic]
        private static int lastError;

        internal bool FailMouseRegistration
        {
            get; set;
        }

        internal bool DelayRelease
        {
            get; set;
        }

        internal int ActiveCount => active.Count;

        internal ManualResetEventSlim AllowRelease { get; } = new();

        internal TaskCompletionSource ReleaseEntered
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 直前の実APIのエラーを呼出しスレッドごとに保持します。
        /// </summary>
        public int LastError => lastError;

        /// <summary>
        /// 実APIで登録し、指定条件では無効な種類を渡してOSの失敗を確認します。
        /// </summary>
        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            callback = WrapCallback(type, callback);
            nint handle = native.Register(FailMouseRegistration && type == 14 ? int.MaxValue : type, callback);
            lastError = handle == 0 ? native.LastError : 0;
            if (handle != 0)
            {
                handles[type] = handle;
                active[handle] = type;
            }

            calls.Enqueue(new("register", type, handle, handle != 0, lastError, Environment.TickCount64));
            return handle;
        }

        /// <summary>
        /// 一度だけ解放直前を遅延し、その後は実APIの返値をそのまま返します。
        /// </summary>
        public bool Unregister(nint hook)
        {
            if (DelayRelease && Interlocked.CompareExchange(ref delayed, 1, 0) == 0)
            {
                ReleaseEntered.SetResult();
                if (!AllowRelease.Wait(TimeSpan.FromSeconds(12)))
                {
                    throw new TimeoutException("検証用の解放遅延期限");
                }
            }

            return Release(hook, "unregister");
        }

        internal bool RemoveKeyboard() => Release(handles[13], "external-unregister");

        private bool Release(nint hook, string operation)
        {
            bool result = native.Unregister(hook);
            lastError = result ? 0 : native.LastError;
            if (result || lastError == 1404)
            {
                active.TryRemove(hook, out _);
            }

            calls.Enqueue(new(operation, 0, hook, result, lastError, Environment.TickCount64));
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice
    {
        internal ushort Page;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    private static RawDevice[] Registrations()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RawDevice>();
        Require(GetRegisteredRawInputDevices(null, ref count, size) != uint.MaxValue,
            "Raw Input登録数取得");
        if (count == 0)
        {
            return [];
        }

        var devices = new RawDevice[count];
        Require(GetRegisteredRawInputDevices(devices, ref count, size) != uint.MaxValue,
            "Raw Input登録一覧取得");
        return [.. devices.Take((int)count).Where(device => device.Page == 1 && device.Usage is 2 or 6)];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices,
        ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
}
