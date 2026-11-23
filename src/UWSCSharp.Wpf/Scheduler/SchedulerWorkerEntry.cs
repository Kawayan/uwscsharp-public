using System.Globalization;
using System.IO;
using System.IO.Pipes;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 専用パイプで準備と開始を確認してから、編集画面なしで実行します。
/// </summary>
internal static class SchedulerWorkerEntry
{
    private static readonly TimeSpan InputRecoveryTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync(string[] options)
    {
        if (options is not [string pipe, string generation, string occurrence,
            string parent, string logDirectory, string keyboardPipe]
            || !Guid.TryParseExact(generation, "N", out Guid generationId)
            || !Guid.TryParseExact(occurrence, "N", out Guid occurrenceId)
            || !int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parentId)
            || parentId <= 0 || !Path.IsPathFullyQualified(logDirectory))
        {
            throw new ArgumentException("予約workerの起動引数が不正です。", nameof(options));
        }
        var identity = new SchedulerWorkerIdentity(generationId, occurrenceId);
        using NamedPipeClientStream client = await SchedulerWorkerPipe.ConnectAsync(pipe);
        if (SchedulerPipeIdentity.Server(client.SafePipeHandle) != (uint)parentId)
        {
            throw new InvalidDataException("予約workerの接続先PIDが一致しません。");
        }
        using var channel = new SchedulerWorkerChannel(client, identity);
        SchedulerWorkerMessage.Request request = await channel.ReadAsync() as SchedulerWorkerMessage.Request
            ?? throw new InvalidDataException("予約workerの最初の通知が要求ではありません。");
        if (System.Windows.Application.Current.Windows.OfType<MainWindow>().Any())
        {
            throw new InvalidOperationException("予約workerに編集画面が作られました。");
        }
        using var keyboardLifetime = new CancellationTokenSource();
        using NamedPipeClientStream keyboardStream = await SchedulerWorkerPipe.ConnectAsync(keyboardPipe);
        if (SchedulerPipeIdentity.Server(keyboardStream.SafePipeHandle) != (uint)parentId)
        {
            throw new InvalidDataException("キー送信の接続先PIDが一致しません。");
        }
        using var keyboardClient = new SchedulerKeyboardClient(keyboardStream, identity, keyboardLifetime.Token);
        using var owner = new SchedulerWorkerPlaybackHost(keyboardClient);
        var cleanup = new InputCleanupController(() => owner.Cleanup.PendingCount,
            async () =>
            {
                WindowsSelectiveInputCleanupResult result = await owner.Cleanup.RetryAsync();
                return new InputCleanupSnapshot(result.Errors, result.PendingCount);
            });
        await channel.SendAsync(new SchedulerWorkerMessage.Ready(identity, Environment.ProcessId));
        using var lifetime = new CancellationTokenSource();
        Task heartbeat = channel.RunHeartbeatAsync(lifetime.Token);
        try
        {
            SchedulerWorkerMessage command;
            using (var preparation = new CancellationTokenSource(
                SchedulerWorkerProtocol.CommunicationTimeout))
            {
                do
                {
                    command = await channel.ReadAsync(preparation.Token);
                }
                while (command is SchedulerWorkerMessage.Heartbeat);
            }
            if (command is SchedulerWorkerMessage.Cancel)
            {
                await channel.SendAsync(new SchedulerWorkerMessage.Completed(
                    identity, 130, true, true, null));
                return 130;
            }
            if (command is not SchedulerWorkerMessage.Start)
            {
                throw new InvalidDataException("予約workerの開始指示が不正です。");
            }
            var storage = new SchedulerManagedStorage(logDirectory);
            using var execution = new SchedulerScriptPlayback(owner, request.Path,
                request.Arguments, storage.LogPath(identity.OccurrenceId), storage,
                request.WindowTrigger);
            owner.Execution = execution;
            await channel.SendAsync(new SchedulerWorkerMessage.Started(identity));
            using var controlsLifetime = new CancellationTokenSource();
            Task<Exception?> controls = ReceiveControlsAsync();
            Task<SchedulerScriptPlaybackResult> running = execution.RunAsync();
            try
            {
                Task first = await Task.WhenAny(running, controls, heartbeat);
                if (first != running)
                {
                    execution.RequestStop();
                }
                SchedulerScriptPlaybackResult result = await running;
                if (controls.IsCompleted && await controls is Exception controlError)
                {
                    throw new IOException("予約workerの制御通信が失われました。", controlError);
                }
                if (heartbeat.IsFaulted)
                {
                    await heartbeat;
                }
                using var recovery = new CancellationTokenSource(InputRecoveryTimeout);
                InputCleanupSnapshot snapshot;
                do
                {
                    snapshot = await cleanup.RefreshAsync();
                    if (cleanup.CanStart || !cleanup.HasKnownCount)
                    {
                        break;
                    }
                    try
                    {
                        await Task.Delay(200, recovery.Token);
                    }
                    catch (OperationCanceledException) when (recovery.IsCancellationRequested)
                    {
                        break;
                    }
                }
                while (!recovery.IsCancellationRequested);
                string? diagnostic = result.Diagnostic;
                if (snapshot.Errors.Count != 0)
                {
                    diagnostic = string.Join(Environment.NewLine,
                        new[] { diagnostic }.Concat(snapshot.Errors.Select(error => ScriptLogHost.FormatDiagnostic(error))));
                }
                await channel.SendAsync(new SchedulerWorkerMessage.Completed(identity,
                    result.ExitCode, result.CancellationCompleted, cleanup.CanStart,
                    SchedulerWorkerDiagnostic.Limit(diagnostic)));
                return result.ExitCode;
            }
            finally
            {
                controlsLifetime.Cancel();
                await controls;
                owner.Execution = null;
            }

            async Task<Exception?> ReceiveControlsAsync()
            {
                try
                {
                    while (true)
                    {
                        SchedulerWorkerMessage next = await channel.ReadAsync(controlsLifetime.Token);
                        if (next is SchedulerWorkerMessage.Cancel)
                        {
                            execution.RequestStop();
                        }
                        else if (next is not SchedulerWorkerMessage.Heartbeat)
                        {
                            throw new InvalidDataException("実行中の予約workerに不正な制御が届きました。");
                        }
                    }
                }
                catch (OperationCanceledException) when (controlsLifetime.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception error)
                {
                    execution.RequestStop();
                    return error;
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await heartbeat;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException
                or ObjectDisposedException)
            {
                App.ReportCommandLineError(error);
            }
        }
    }
}
