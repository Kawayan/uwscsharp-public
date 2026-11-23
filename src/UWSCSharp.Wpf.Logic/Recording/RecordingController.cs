namespace UWSCSharp.Wpf;

internal interface IRecordingDevice : IDisposable
{
    bool IsRunning
    {
        get;
    }

    bool WaitingForInitialRelease
    {
        get;
    }

    bool Overflowed
    {
        get;
    }

    Task StartAsync();

    string Stop();

    string Warning => "";
}

internal enum RecordingStopReason
{
    Manual,
    OwnerClosing,
    WorkerStopped
}

internal readonly record struct RecordingResult(
    string Source, bool Overflowed, string Warning,
    bool CopyToClipboard = false);

internal readonly record struct RecordingUiState(
    bool CanEdit, bool CanRecord, bool CanStop, bool Monitor, string RecordLabel);

// 記録の開始・停止と、期限超過後の監視・再試行状態を管理します。
internal sealed class RecordingController(
    Func<IRecordingDevice> factory, TimeSpan startTimeout)
{
    private IRecordingDevice? current;
    private bool copyOnFinish;

    internal bool HasRecorder => current is not null;

    internal bool IsStarting
    {
        get; private set;
    }

    internal bool IsStopping
    {
        get; private set;
    }

    internal bool RecoveryPending
    {
        get; private set;
    }

    internal bool IsRunning => current?.IsRunning == true;

    internal bool WaitingForInitialRelease => current?.WaitingForInitialRelease == true;

    internal string Warning => current?.Warning ?? "";

    internal RecordingUiState UiState => new(
        !HasRecorder && !IsStarting && !IsStopping,
        !IsStarting && !IsStopping,
        HasRecorder && !IsStarting && !IsStopping,
        HasRecorder && !IsStarting && !IsStopping,
        HasRecorder ? "■ 記録終了" : "● 記録");

    internal Task StartAsync() => StartAsync(copyAfterRecording: false);

    internal async Task StartAsync(bool copyAfterRecording)
    {
        if (HasRecorder || IsStarting || IsStopping)
        {
            throw new InvalidOperationException("記録処理が終了するまで再開始できません。");
        }

        IsStarting = true;
        RecoveryPending = false;
        copyOnFinish = false;
        Task? pending = null;
        try
        {
            current = factory();
            pending = current.StartAsync();
            await pending.WaitAsync(startTimeout);
            copyOnFinish = copyAfterRecording;
        }
        catch (Exception startError)
        {
            if (pending is not null)
            {
                // 開始期限後に返る失敗も観測し、未観測例外を残しません。
                _ = ObserveStartAsync(pending);
            }

            Exception? cleanupError = null;
            if (current is { } device)
            {
                try
                {
                    await Task.Run(device.Dispose);
                }
                catch (Exception error)
                {
                    cleanupError = error;
                }

                RecoveryPending = device.IsRunning;
                if (!RecoveryPending)
                {
                    current = null;
                }
            }

            if (cleanupError is not null)
            {
                throw new AggregateException(startError, cleanupError);
            }

            throw;
        }
        finally
        {
            IsStarting = false;
        }
    }

    internal Task<RecordingResult?> StopAsync()
        => StopAsync(RecordingStopReason.Manual);

    internal async Task<RecordingResult?> StopAsync(RecordingStopReason reason)
    {
        if (current is not { } device || IsStarting || IsStopping)
        {
            return null;
        }

        IsStopping = true;
        // 失敗後の停止再試行や要求の重複へ、コピーの権限を持ち越しません。
        bool copyRequested = copyOnFinish && reason == RecordingStopReason.Manual;
        copyOnFinish = false;
        try
        {
            string source = await Task.Run(device.Stop);
            bool overflowed = device.Overflowed;
            string warning = device.Warning;
            var result = new RecordingResult(source, overflowed, warning,
                copyRequested && !overflowed && string.IsNullOrEmpty(warning)
                    && !string.IsNullOrWhiteSpace(source));
            current = null;
            RecoveryPending = false;
            return result;
        }
        catch
        {
            RecoveryPending = device.IsRunning;
            if (!RecoveryPending)
            {
                current = null;
            }

            throw;
        }
        finally
        {
            IsStopping = false;
        }
    }

    private static async Task ObserveStartAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // 期限後の失敗は開始処理側で報告済みです。
        }
    }
}
