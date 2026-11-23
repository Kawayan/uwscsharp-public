using System.Globalization;
using System.IO;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal sealed record SchedulerWorkerRunResult(int ProcessId,
    SchedulerWorkerMessage.Completed? Completion, int? ProcessExitCode,
    bool Started, bool Forced, bool ProcessesRecovered, string? Diagnostic,
    SchedulerExecutionResult? EarlyResult = null, bool ParentInputRecovered = true)
{
    internal bool ResultKnown => EarlyResult is not null
        ? EarlyResult.Outcome != SchedulerExecutionOutcome.Unknown
        : !Forced && ProcessesRecovered
        && Completion is not null && ProcessExitCode == Completion.ExitCode;

    internal bool ResourcesRecovered => EarlyResult?.ResourcesRecovered ?? (ProcessesRecovered
        && Completion?.InputRecovered == true && ParentInputRecovered);

    internal SchedulerExecutionResult Result => EarlyResult ?? (ResultKnown
        ? SchedulerExecutionResult.Completed(Completion!.ExitCode, Started,
            Completion.CancellationCompleted, ResourcesRecovered, CombinedDiagnostic)
        : SchedulerExecutionResult.Unknown(Started, ProcessExitCode,
            ResourcesRecovered, CombinedDiagnostic, Forced));

    private string? CombinedDiagnostic => string.IsNullOrEmpty(Diagnostic)
        ? Completion?.Diagnostic : string.IsNullOrEmpty(Completion?.Diagnostic)
        ? Diagnostic : Diagnostic + Environment.NewLine + Completion.Diagnostic;

}

/// <summary>
/// 所有Jobでworkerを起動し、通信とプロセス回収を別々に確認します。
/// </summary>
internal static class SchedulerWorkerClient
{
    private static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChildDrainTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<SchedulerWorkerRunResult> RunAsync(
        SchedulerProcessGroup group, string executable, SchedulerDispatch dispatch,
        string logDirectory, CancellationToken cancellation = default,
        Action<int>? onReady = null,
        Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard = null,
        Action? onStarted = null,
        Action<Action>? onControlConnected = null)
    {
        if (dispatch.Request.Entry.Mode != SchedulerExecutionMode.SeparateProcess)
        {
            throw new ArgumentException("別プロセス方式の予約が必要です。", nameof(dispatch));
        }
        var creation = new SchedulerProcessCreation();
        bool observedStarted = false;
        try
        {
            return await RunCoreAsync(group, executable, dispatch, logDirectory,
                cancellation, onReady, creation, keyboardGuard, () =>
                {
                    observedStarted = true;
                    onStarted?.Invoke();
                }, onControlConnected);
        }
        catch (Exception error)
        {
            string diagnostic = ScriptLogHost.FormatDiagnostic(error);
            SchedulerExecutionResult early = !creation.Created
                ? error is OperationCanceledException && cancellation.IsCancellationRequested
                    ? SchedulerExecutionResult.Completed(130, false, true, true, diagnostic)
                    : SchedulerExecutionResult.FailedToStart(diagnostic)
                : SchedulerExecutionResult.Unknown(observedStarted, null, false, diagnostic);
            return new(checked((int)creation.ProcessId), null, null, observedStarted,
                false, !creation.Created, diagnostic, early);
        }
    }

    // 接続前も子の終了・取消・通信期限を監視し、未接続の待機を残しません。
    private static async Task WaitForConnectionAsync(
        SchedulerWorkerPipe pipe, SchedulerChildProcess child,
        CancellationToken cancellation)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task connecting = pipe.WaitForConnectionAsync(waiting.Token);
        Task<uint> exited = child.WaitForExitAsync(waiting.Token);
        try
        {
            await Task.WhenAny(connecting, exited);
            if (!connecting.IsCompleted && exited.IsCompletedSuccessfully)
            {
                throw new IOException("予約workerが接続前に終了しました。終了コード: " + await exited);
            }
            await connecting;
        }
        finally
        {
            waiting.Cancel();
            try
            {
                await connecting;
            }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<SchedulerWorkerRunResult> RunCoreAsync(
        SchedulerProcessGroup group, string executable, SchedulerDispatch dispatch,
        string logDirectory, CancellationToken cancellation,
        Action<int>? onReady, SchedulerProcessCreation creation,
        Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard, Action? onStarted,
        Action<Action>? onControlConnected)
    {
        cancellation.ThrowIfCancellationRequested();
        string logs = Path.GetFullPath(logDirectory);
        var identity = new SchedulerWorkerIdentity(dispatch.Generation, dispatch.Request.OccurrenceId);
        using var pipe = new SchedulerWorkerPipe();
        using var keyboardPipe = new SchedulerWorkerPipe();
        using SchedulerChildProcess child = group.Start(executable,
            ["--scheduler-worker", pipe.Name, identity.Generation.ToString("N"),
                identity.OccurrenceId.ToString("N"),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture), logs, keyboardPipe.Name],
            dispatch.Request.Entry.WorkingDirectory, cancellation, creation);
        var handshake = new SchedulerWorkerHandshake(identity, checked((int)child.Id));
        using var lifetime = new CancellationTokenSource();
        using var keyboardLifetime = new CancellationTokenSource();
        var keyboardConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SchedulerKeyboardServer? keyboard = null;
        Exception? keyboardError = null;
        Task keyboardTask = ReceiveKeyboardAsync();
        SchedulerWorkerChannel? channel = null;
        Task? heartbeat = null;
        Task<SchedulerWorkerMessage>? reading = null;
        try
        {
            await WaitForConnectionAsync(pipe, child, cancellation);
            if (SchedulerPipeIdentity.Client(pipe.Stream.SafePipeHandle) != child.Id)
            {
                throw new InvalidDataException("予約workerの接続元PIDが一致しません。");
            }
            channel = new SchedulerWorkerChannel(pipe.Stream, identity);
            // 診断には、この実行の制御通信を閉じる操作だけを渡します。
            onControlConnected?.Invoke(pipe.Dispose);
            await channel.SendAsync(SchedulerWorkerMessage.Request.FromDispatch(dispatch), lifetime.Token);
            handshake.AcceptReady(await channel.ReadAsync(lifetime.Token)
                as SchedulerWorkerMessage.Ready
                ?? throw new InvalidDataException("予約workerの準備通知が不正です。"));
            if (!await keyboardConnected.Task.WaitAsync(SchedulerWorkerProtocol.CommunicationTimeout))
            {
                throw keyboardError ?? new IOException("キー送信の通信路を準備できません。");
            }
            heartbeat = channel.RunHeartbeatAsync(lifetime.Token);
            onReady?.Invoke((int)child.Id);
            if (cancellation.IsCancellationRequested)
            {
                handshake.RequestCancellation();
            }
            SchedulerWorkerMessage command = handshake.CreateNextCommand();
            await channel.SendAsync(command, lifetime.Token);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellation.Register(
                () => cancelled.TrySetResult());
            Task pendingCancellation = cancelled.Task;
            Task grace = command is SchedulerWorkerMessage.Cancel
                ? Task.Delay(CancellationGracePeriod, lifetime.Token)
                : Task.Delay(Timeout.Infinite, lifetime.Token);
            SchedulerWorkerMessage.Completed? completion = null;
            while (completion is null)
            {
                reading ??= channel.ReadAsync(lifetime.Token);
                Task next = await Task.WhenAny(reading, pendingCancellation, grace, heartbeat);
                if (next == grace)
                {
                    return await ForceAsync("予約workerの協調取消が5秒以内に完了しませんでした。");
                }
                if (next == heartbeat)
                {
                    await heartbeat;
                    throw new IOException("予約workerの生存通知が停止しました。");
                }
                if (next == pendingCancellation)
                {
                    SchedulerWorkerMessage.Cancel? cancel = handshake.RequestCancellation();
                    if (cancel is not null)
                    {
                        await channel.SendAsync(cancel, lifetime.Token);
                        grace = Task.Delay(CancellationGracePeriod, lifetime.Token);
                    }
                    pendingCancellation = Task.Delay(Timeout.Infinite, lifetime.Token);
                    continue;
                }
                SchedulerWorkerMessage message = await reading;
                reading = null;
                switch (message)
                {
                    case SchedulerWorkerMessage.Started started:
                        handshake.AcceptStarted(started);
                        onStarted?.Invoke();
                        break;
                    case SchedulerWorkerMessage.Completed completed:
                        handshake.AcceptCompletion(completed);
                        completion = completed;
                        break;
                    case SchedulerWorkerMessage.Heartbeat:
                        break;
                    default:
                        throw new InvalidDataException("予約workerから不正な実行通知が届きました。");
                }
            }
            keyboardLifetime.Cancel();
            await keyboardTask;
            using var cleanup = new CancellationTokenSource(ChildDrainTimeout);
            uint code = await child.WaitForDrainAsync(cleanup.Token);
            int exitCode = unchecked((int)code);
            return new((int)child.Id, completion, exitCode, handshake.HasStarted,
                false, true, exitCode == completion.ExitCode
                    ? keyboardError?.Message : "予約workerの完了通知とプロセス終了コードが一致しません。",
                ParentInputRecovered: keyboard?.Recovered == true && keyboardError is null);
        }
        catch (Exception error)
        {
            handshake.Fault();
            return await ForceAsync(ScriptLogHost.FormatDiagnostic(error));
        }
        finally
        {
            lifetime.Cancel();
            keyboardLifetime.Cancel();
            pipe.Dispose();
            await keyboardTask;
            keyboardPipe.Dispose();
            if (reading is not null)
            {
                try
                {
                    await reading;
                }
                catch (Exception error)
                {
                    App.ReportCommandLineError(error);
                }
            }
            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat;
                }
                catch (Exception error)
                {
                    App.ReportCommandLineError(error);
                }
            }
            channel?.Dispose();
        }

        async Task ReceiveKeyboardAsync()
        {
            try
            {
                await keyboardPipe.WaitForConnectionAsync(keyboardLifetime.Token);
                if (SchedulerPipeIdentity.Client(keyboardPipe.Stream.SafePipeHandle) != child.Id)
                {
                    throw new InvalidDataException("キー送信の接続元PIDが一致しません。");
                }
                keyboard = new SchedulerKeyboardServer(keyboardPipe.Stream, identity, keyboardGuard);
                keyboardConnected.TrySetResult(true);
                await keyboard.RunAsync(keyboardLifetime.Token);
                keyboardError = keyboard.Failure;
            }
            catch (OperationCanceledException) when (keyboardLifetime.IsCancellationRequested)
            {
                keyboardConnected.TrySetCanceled(keyboardLifetime.Token);
            }
            catch (Exception error)
            {
                keyboardError = error;
                keyboardConnected.TrySetResult(false);
            }
        }

        async Task<SchedulerWorkerRunResult> ForceAsync(string reason)
        {
            keyboardLifetime.Cancel();
            string diagnostic = reason;
            try
            {
                child.TerminateTree();
            }
            catch (Exception error)
            {
                diagnostic += Environment.NewLine + "予約Jobの停止: " + ScriptLogHost.FormatDiagnostic(error);
            }
            try
            {
                using var cleanup = new CancellationTokenSource(ChildDrainTimeout);
                uint code = await child.WaitForDrainAsync(cleanup.Token);
                return new((int)child.Id, null, unchecked((int)code),
                    handshake.HasStarted, true, true, diagnostic);
            }
            catch (Exception error)
            {
                return new((int)child.Id, null, null, handshake.HasStarted,
                    true, false, diagnostic + Environment.NewLine
                        + "予約Jobの回収: " + ScriptLogHost.FormatDiagnostic(error));
            }
        }
    }
}
