using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// 非表示の実WPF部品と差し替えた記録器で、失敗後の操作状態を確認します。
internal static class RecordingUiVerification
{
    internal static async Task VerifyAsync(ICollection<string> checks)
    {
        await VerifyStartFailureAsync();
        checks.Add("recording-start-failure-preserves-source-and-restarts");
        await VerifyPendingStartAsync();
        checks.Add("recording-pending-start-close-and-timeout-recovery");
        await VerifyPendingStopAsync();
        checks.Add("recording-pending-stop-close-and-retry");
    }

    private static async Task VerifyStartFailureAsync()
    {
        var failure = new IOException("検証用の記録開始失敗");
        await using var screen = new Screen(new Device { StartFailure = failure });
        screen.Record();
        await WaitAsync(() => screen.Errors.Count == 1);
        Require(ReferenceEquals(failure, screen.Errors[0]), "開始エラーの保持");
        screen.RequireIdle();
        screen.RequireOriginal();
        screen.Device = new Device();
        screen.Record();
        await WaitAsync(() => screen.Controller.IsRunning);
        screen.Stop();
        await WaitAsync(() => !screen.Controller.HasRecorder);
        screen.RequireIdle();
        Require(screen.Editor.Text == Device.Result, "再記録の結果反映");
    }

    private static async Task VerifyPendingStartAsync()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var screen = new Screen(new Device
        {
            Starting = pending,
            DisposeFailure = new TimeoutException("検証用の解放待ち")
        });
        screen.Record();
        Require(screen.Controller.IsStarting, "開始待機");
        Require(!screen.RecordButton.IsEnabled && !screen.StopButton.IsEnabled
            && screen.Editor.IsReadOnly, "開始中の操作抑止");
        int confirmations = screen.Confirmations;
        screen.RequestClose();
        Require(!screen.Closed && screen.CloseCanceled
            && screen.Confirmations == confirmations, "開始中の終了保留");
        await WaitAsync(() => screen.Errors.Count == 1);
        Require(screen.Errors[0] is AggregateException aggregate
            && aggregate.InnerExceptions.All(error => error is TimeoutException),
            "開始と解放の期限超過");
        screen.RequireRecovery();
        screen.RequireOriginal();
        pending.SetException(new IOException("検証用の遅延開始失敗"));
        screen.Stop();
        await WaitAsync(() => !screen.Controller.HasRecorder);
        screen.RequireIdle();
    }

    private static async Task VerifyPendingStopAsync()
    {
        using var release = new ManualResetEventSlim();
        await using var screen = new Screen(new Device
        {
            StopGate = release,
            StopFailure = new TimeoutException("検証用の停止待ち")
        });
        screen.Record();
        await WaitAsync(() => screen.Controller.IsRunning);
        screen.Stop();
        Require(screen.Controller.IsStopping, "停止待機");
        Require(!screen.RecordButton.IsEnabled && !screen.StopButton.IsEnabled
            && screen.Editor.IsReadOnly, "停止中の操作抑止");
        screen.RequestClose();
        Require(!screen.Closed && screen.CloseCanceled, "停止中の終了保留");
        release.Set();
        await WaitAsync(() => screen.Errors.Count == 1);
        Require(screen.Errors[0] is TimeoutException, "停止エラーの保持");
        screen.RequireRecovery();
        screen.RequireOriginal();
        screen.Device.StopFailure = null;
        screen.RequestClose();
        await WaitAsync(() => screen.Closed);
        screen.RequireIdle();
        Require(screen.Editor.Text == Device.Result, "停止再試行の結果反映");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(5), "WPF状態遷移の期限");
            await Task.Delay(10);
        }
    }

    private sealed class Screen : IAsyncDisposable
    {
        private const string Original = "PRINT \"元の未保存ソース\"";
        private readonly MainWindow window;

        internal Screen(Device device)
        {
            Device = device;
            Controller = new RecordingController(() => Device,
                TimeSpan.FromMilliseconds(200));
            window = new MainWindow(false, recordingController: Controller,
                confirmDiscard: () =>
                {
                    Confirmations++;
                    return true;
                },
                reportError: Errors.Add);
            Editor.Text = Original;
            window.Closing += (_, args) => CloseCanceled = args.Cancel;
            window.Closed += (_, _) => Closed = true;
        }

        internal Device Device
        {
            get; set;
        }

        internal RecordingController Controller
        {
            get;
        }

        internal List<Exception> Errors { get; } = [];

        internal int Confirmations
        {
            get; private set;
        }

        internal bool Closed
        {
            get; private set;
        }

        internal bool CloseCanceled
        {
            get; private set;
        }

        internal TextBox Editor => (TextBox)window.FindName("Editor");

        internal Button RecordButton => (Button)window.FindName("RecordButton");

        internal Button StopButton => (Button)window.FindName("StopButton");

        internal void Record() => RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void Stop() => StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void RequestClose() => window.Close();

        internal void RequireIdle()
        {
            Require(!Controller.HasRecorder && !Controller.IsStarting
                && !Controller.IsStopping && RecordButton.IsEnabled
                && !StopButton.IsEnabled && !Editor.IsReadOnly
                && ((Button)window.FindName("PlayButton")).IsEnabled,
                "編集・再操作への復帰");
        }

        internal void RequireRecovery()
        {
            Require(Controller.RecoveryPending && RecordButton.IsEnabled
                && StopButton.IsEnabled && Editor.IsReadOnly
                && !((Button)window.FindName("PlayButton")).IsEnabled,
                "監視と停止再試行への復帰");
        }

        internal void RequireOriginal() => Require(Editor.Text == Original,
            "失敗時の未保存ソース保持");

        /// <summary>
        /// 検証用の待機を解除し、所有する画面を閉じます。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Device.Starting?.TrySetResult();
            Device.StopGate?.Set();
            Device.DisposeFailure = null;
            Device.StopFailure = null;
            await WaitAsync(() => !Controller.IsStarting && !Controller.IsStopping);
            if (Controller.HasRecorder)
            {
                await Controller.StopAsync();
            }

            if (!Closed)
            {
                window.Close();
            }
        }
    }

    private sealed class Device : IRecordingDevice
    {
        internal const string Result = "PRINT \"検証用の記録結果\"";

        internal TaskCompletionSource? Starting
        {
            get; init;
        }

        internal Exception? StartFailure
        {
            get; init;
        }

        internal Exception? DisposeFailure
        {
            get; set;
        }

        internal Exception? StopFailure
        {
            get; set;
        }

        internal ManualResetEventSlim? StopGate
        {
            get; init;
        }

        /// <summary>
        /// 検証用記録器が動作中かを取得します。
        /// </summary>
        public bool IsRunning
        {
            get; private set;
        }

        /// <summary>
        /// 検証用記録器では保持入力の解放を待ちません。
        /// </summary>
        public bool WaitingForInitialRelease => false;

        /// <summary>
        /// 検証用記録器では入力を収集しません。
        /// </summary>
        public bool Overflowed => false;

        /// <summary>
        /// 指定された開始失敗または待機を返します。
        /// </summary>
        public Task StartAsync()
        {
            IsRunning = true;
            return StartFailure is { } failure ? Task.FromException(failure)
                : Starting?.Task ?? Task.CompletedTask;
        }

        /// <summary>
        /// 停止失敗と復旧の条件を再現します。
        /// </summary>
        public string Stop()
        {
            if (StopGate?.Wait(TimeSpan.FromSeconds(5)) == false)
            {
                throw new TimeoutException("検証用の停止解放期限");
            }

            if (StopFailure is { } failure)
            {
                throw failure;
            }

            IsRunning = false;
            return Result;
        }

        /// <summary>
        /// 解放失敗時は動作中の状態を保持します。
        /// </summary>
        public void Dispose()
        {
            if (DisposeFailure is { } failure)
            {
                throw failure;
            }

            IsRunning = false;
        }
    }
}
