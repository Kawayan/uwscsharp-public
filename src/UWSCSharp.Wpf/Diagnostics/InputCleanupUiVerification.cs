using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

// 実WPF部品へ偽の回収対象を注入し、画面を表示せずに操作制限を確認します。
internal static class InputCleanupUiVerification
{
    internal static async Task VerifyAsync(ICollection<string> checks)
    {
        await VerifyBlockedStartsAsync();
        checks.Add("input-cleanup-blocks-play-record-drop-and-argument-launch");
        await VerifyManualRetryAsync();
        checks.Add("input-cleanup-manual-retry-preserves-errors-and-count");
        await VerifyBusyAndCloseAsync();
        checks.Add("input-cleanup-background-query-and-close-confirmation");
        await VerifyRecordingRefreshAndCompletionAsync();
        checks.Add("input-cleanup-recording-refresh-and-passive-completion");
    }

    private static async Task VerifyBlockedStartsAsync()
    {
        await using var screen = new Screen(initialCount: 1);
        string source = screen.Editor.Text;
        Require(!screen.Play.IsEnabled && !screen.Record.IsEnabled
            && screen.Retry.IsEnabled, "未回収時の開始制限と再回収");
        Require(!screen.Editor.IsReadOnly && screen.Button("SaveButton").IsEnabled,
            "未回収中も編集と保存を維持");
        screen.Play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        screen.Record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await screen.Window.RunDroppedScriptAsync("読み込まない.uws") is null,
            "未回収中のドロップ拒否");
        Require(screen.Editor.Text == source && screen.RetryCount == 0,
            "拒否時のソース保持と自動再回収なし");
        int code = await screen.Window.RunCommandLineAsync("PRINT 999",
            new Core.ScriptRunOptions([]));
        Require(code == 2 && screen.RetryCount == 0,
            "引数起動でも未回収中は実行しない");
    }

    private static async Task VerifyManualRetryAsync()
    {
        await using var screen = new Screen(initialCount: 1);
        var completion = new TaskCompletionSource<InputCleanupSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        screen.RetryCompletion = completion;
        screen.RetryResult = completion.Task;
        screen.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => screen.RetryCount == 1);
        Require(!screen.Retry.IsEnabled && !screen.Play.IsEnabled
            && !screen.Record.IsEnabled, "再回収中の要求と開始の抑止");
        screen.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        completion.SetResult(new InputCleanupSnapshot(
            [new IOException("検証用の過去の解除失敗")], 1));
        await WaitAsync(() => screen.Retry.IsEnabled);
        Require(screen.RetryCount == 1 && !screen.Play.IsEnabled
            && screen.Text("Output").Contains("検証用の過去の解除失敗"),
            "診断を表示しても未回収を完了扱いにしない");
        screen.Pending = 0;
        screen.RetryResult = Task.FromResult(new InputCleanupSnapshot(
            [new IOException("検証用の過去の解除失敗")], 0));
        screen.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => screen.Play.IsEnabled);
        Require(screen.RetryCount == 2 && screen.Record.IsEnabled
            && !screen.Retry.IsEnabled && screen.Text("InputCleanupLabel").Contains("回収完了"),
            "件数0で開始を復帰");
        Require(screen.Text("Output").Contains("検証用の過去の解除失敗"),
            "完了後も過去の診断を保持");
    }

    private static async Task VerifyBusyAndCloseAsync()
    {
        await using var screen = new Screen(initialCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        screen.CountPending = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("検証用の照会解放期限");
            }

            return 1;
        };
        Task checking = screen.Window.RefreshInputCleanupAsync();
        try
        {
            await WaitAsync(() => entered.IsSet);
            bool responded = false;
            _ = screen.Window.Dispatcher.BeginInvoke(() => responded = true);
            await WaitAsync(() => responded);
            screen.Window.Close();
            Require(!screen.Closed && screen.ExitConfirmations == 0,
                "照会中は画面応答を維持して終了を保留");
        }
        finally
        {
            release.Set();
        }

        await checking;
        screen.CountPending = null;
        screen.Window.Close();
        Require(!screen.Closed && screen.ExitConfirmations == 1,
            "未回収の終了確認から画面へ戻る");
        screen.AllowExit = true;
        screen.Window.Close();
        Require(screen.Closed && screen.ExitConfirmations == 2 && screen.RetryCount == 0,
            "未回収の明示終了でも自動再回収しない");
    }

    private static async Task VerifyRecordingRefreshAndCompletionAsync()
    {
        var device = new Device();
        var recording = new RecordingController(() => device, TimeSpan.FromSeconds(3));
        await using var screen = new Screen(initialCount: 0, recording: recording);
        screen.Record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => recording.IsRunning);
        // 遅延した完了通知を想定し、記録終了側のUI更新にも制限を適用します。
        screen.Pending = 1;
        await screen.Window.RefreshInputCleanupAsync();
        screen.Button("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => !recording.HasRecorder && !screen.Editor.IsReadOnly);
        Require(!screen.Play.IsEnabled && !screen.Record.IsEnabled && screen.Retry.IsEnabled,
            "記録終了のUI更新でも未回収の開始制限を維持");
        screen.Pending = 0;
        await WaitAsync(() => screen.Play.IsEnabled && screen.Record.IsEnabled);
        Require(screen.RetryCount == 0 && !screen.Retry.IsEnabled,
            "状態照会だけで別経路の回収完了を反映");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("入力フック回収画面の検証期限");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Screen : IAsyncDisposable
    {
        internal Screen(int initialCount, RecordingController? recording = null)
        {
            Pending = initialCount;
            var controller = new InputCleanupController(
                () => CountPending?.Invoke() ?? Pending, () =>
                {
                    Interlocked.Increment(ref retryCount);
                    return RetryResult ?? Task.FromResult(new InputCleanupSnapshot([], Pending));
                }, initialCount);
            Window = new MainWindow(false, recordingController: recording,
                cleanupController: controller, confirmDiscard: () => true,
                reportError: error => throw new InvalidOperationException("画面の検証エラー", error),
                confirmPendingCleanupExit: () =>
                {
                    ExitConfirmations++;
                    return AllowExit;
                });
            Window.Closed += (_, _) => Closed = true;
            Editor.Text = "PRINT \"未保存の検証用ソース\"";
        }

        private int retryCount;

        internal MainWindow Window
        {
            get;
        }

        internal int Pending;

        internal int RetryCount => Volatile.Read(ref retryCount);

        internal Func<int>? CountPending
        {
            get; set;
        }

        internal Task<InputCleanupSnapshot>? RetryResult
        {
            get; set;
        }

        internal TaskCompletionSource<InputCleanupSnapshot>? RetryCompletion
        {
            get; set;
        }

        internal int ExitConfirmations
        {
            get; private set;
        }

        internal bool AllowExit
        {
            get; set;
        }

        internal bool Closed
        {
            get; private set;
        }

        internal TextBox Editor => (TextBox)Window.FindName("Editor");

        internal Button Play => Button("PlayButton");

        internal Button Record => Button("RecordButton");

        internal Button Retry => Button("InputCleanupButton");

        internal Button Button(string name) => (Button)Window.FindName(name);

        internal string Text(string name) => Window.FindName(name) switch
        {
            TextBox text => text.Text,
            TextBlock text => text.Text,
            _ => throw new InvalidOperationException(name)
        };

        /// <summary>
        /// 偽の未回収状態を解消して、所有する画面を閉じます。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (!Closed)
            {
                Pending = 0;
                CountPending = null;
                RetryCompletion?.TrySetResult(new InputCleanupSnapshot([], 0));
                await Window.RefreshInputCleanupAsync().WaitAsync(TimeSpan.FromSeconds(10));
                AllowExit = true;
                Window.Close();
            }
        }
    }

    private sealed class Device : IRecordingDevice
    {
        /// <summary>
        /// 検証用記録器の実行状態を返します。
        /// </summary>
        public bool IsRunning
        {
            get; private set;
        }

        /// <summary>
        /// 実入力の解放待ちは行いません。
        /// </summary>
        public bool WaitingForInitialRelease => false;

        /// <summary>
        /// 検証用記録器では入力を収集しません。
        /// </summary>
        public bool Overflowed => false;

        /// <summary>
        /// OS入力を使わずに開始します。
        /// </summary>
        public Task StartAsync()
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 固定の検証用ソースを返します。
        /// </summary>
        public string Stop()
        {
            IsRunning = false;
            return "PRINT \"検証用の記録結果\"";
        }

        /// <summary>
        /// OS資源を持たない検証用記録器を破棄します。
        /// </summary>
        public void Dispose() => IsRunning = false;
    }
}
