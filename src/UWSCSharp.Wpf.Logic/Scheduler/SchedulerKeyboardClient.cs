using System.IO;
using System.Runtime.ExceptionServices;

namespace UWSCSharp.Wpf;

/// <summary>
/// 親のキー抑制を確認してから送信し、復元の確認まで待ちます。
/// </summary>
internal sealed class SchedulerKeyboardClient(Stream stream,
    SchedulerWorkerIdentity identity, CancellationToken lifetime) : IDisposable
{
    private readonly SemaphoreSlim sending = new(1, 1);

    internal async Task SendAsync(int key, Func<Task> send, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        await sending.WaitAsync(waiting.Token).ConfigureAwait(false);
        try
        {
            var request = new SchedulerWorkerMessage.Keyboard(identity,
                Guid.NewGuid(), key, SchedulerKeyboardPhase.Request);
            bool granted = false;
            bool replyAccepted = false;
            Exception? failure = null;
            try
            {
                await SchedulerWorkerProtocol.WriteAsync(stream, request, waiting.Token).ConfigureAwait(false);
                SchedulerWorkerMessage.Keyboard reply = await ReadReplyAsync(request,
                    SchedulerKeyboardPhase.Granted, waiting.Token).ConfigureAwait(false);
                replyAccepted = true;
                if (!reply.Succeeded)
                {
                    throw new InvalidOperationException(reply.Diagnostic ?? "親がキー送信を許可しませんでした。");
                }
                granted = true;
                cancellationToken.ThrowIfCancellationRequested();
                await send().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
                if (!granted && !replyAccepted)
                {
                    stream.Dispose();
                }
            }
            if (granted)
            {
                try
                {
                    await SchedulerWorkerProtocol.WriteAsync(stream,
                        request with
                        {
                            Phase = SchedulerKeyboardPhase.Release
                        }, lifetime).ConfigureAwait(false);
                    SchedulerWorkerMessage.Keyboard restored = await ReadReplyAsync(request,
                        SchedulerKeyboardPhase.Restored, lifetime).ConfigureAwait(false);
                    if (!restored.Succeeded)
                    {
                        throw new InvalidOperationException(restored.Diagnostic ?? "親のホットキーを復元できません。");
                    }
                }
                catch (Exception error)
                {
                    stream.Dispose();
                    failure = failure is null ? error
                        : new AggregateException("キー送信と親の復元確認が失敗しました。", failure, error);
                }
            }
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            sending.Release();
        }
    }

    private async Task<SchedulerWorkerMessage.Keyboard> ReadReplyAsync(
        SchedulerWorkerMessage.Keyboard request, SchedulerKeyboardPhase phase,
        CancellationToken token)
    {
        SchedulerWorkerMessage.Keyboard reply = await SchedulerWorkerProtocol.ReadAsync(
            stream, identity, token).ConfigureAwait(false) as SchedulerWorkerMessage.Keyboard
            ?? throw new InvalidDataException("キー送信の応答種別が不正です。");
        if (reply.Transaction != request.Transaction || reply.Key != request.Key || reply.Phase != phase)
        {
            throw new InvalidDataException("キー送信の応答が現在の要求と一致しません。");
        }
        return reply;
    }

    public void Dispose() => sending.Dispose();
}
