using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 独立した通信路で、親の抑制・送信許可・復元を直列に扱います。
/// </summary>
internal sealed class SchedulerKeyboardServer(Stream stream,
    SchedulerWorkerIdentity identity,
    Func<int, Func<Task>, CancellationToken, Task>? guard)
{
    internal bool Recovered { get; private set; } = true;

    internal Exception? Failure
    {
        get; private set;
    }

    internal async Task RunAsync(CancellationToken lifetime)
    {
        try
        {
            while (true)
            {
                SchedulerWorkerMessage message;
                try
                {
                    message = await SchedulerWorkerProtocol.ReadAsync(stream, identity,
                        lifetime, waitForMessage: true).ConfigureAwait(false);
                }
                catch (SchedulerIdleConnectionClosedException)
                {
                    return;
                }
                if (message is not SchedulerWorkerMessage.Keyboard
                    { Phase: SchedulerKeyboardPhase.Request } request)
                {
                    throw new InvalidDataException("キー送信の要求種別が不正です。");
                }
                bool entered = false;
                Exception? error = null;
                try
                {
                    if (guard is null || !Recovered)
                    {
                        throw new NotSupportedException("親のキー送信調停を利用できません。");
                    }
                    using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    acquisition.CancelAfter(SchedulerWorkerProtocol.CommunicationTimeout);
                    await guard(request.Key, async () =>
                    {
                        entered = true;
                        await SchedulerWorkerProtocol.WriteAsync(stream,
                            request with
                            {
                                Phase = SchedulerKeyboardPhase.Granted
                            }, lifetime).ConfigureAwait(false);
                        SchedulerWorkerMessage release = await SchedulerWorkerProtocol.ReadAsync(
                            stream, identity, lifetime).ConfigureAwait(false);
                        if (release is not SchedulerWorkerMessage.Keyboard end
                            || end.Phase != SchedulerKeyboardPhase.Release
                            || end.Transaction != request.Transaction || end.Key != request.Key)
                        {
                            throw new InvalidDataException("キー送信の終了通知が一致しません。");
                        }
                    }, acquisition.Token).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    error = failure;
                    if (guard is not null && failure is not OperationCanceledException)
                    {
                        Recovered = false;
                    }
                }
                await SchedulerWorkerProtocol.WriteAsync(stream, request with
                {
                    Phase = entered ? SchedulerKeyboardPhase.Restored : SchedulerKeyboardPhase.Granted,
                    Succeeded = error is null,
                    Diagnostic = SchedulerWorkerDiagnostic.Limit(error?.Message)
                }, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Failure = error;
            Recovered = false;
        }
    }
}
