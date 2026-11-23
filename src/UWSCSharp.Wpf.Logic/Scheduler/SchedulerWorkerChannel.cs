using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 制御・完了・生存通知の書込みを1端点につき直列にします。
/// </summary>
internal sealed class SchedulerWorkerChannel(Stream stream,
    SchedulerWorkerIdentity identity, TimeProvider? clock = null) : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim writer = new(1, 1);
    private Exception? writeFailure;

    internal async Task SendAsync(SchedulerWorkerMessage message,
        CancellationToken token = default)
    {
        if (message.Identity != identity)
        {
            throw new InvalidDataException("別の予約実行の通知は送信できません。");
        }
        using var timeout = new CancellationTokenSource(
            SchedulerWorkerProtocol.CommunicationTimeout,
            clock ?? TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            token, timeout.Token);
        try
        {
            await writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (writeFailure is not null)
                {
                    throw new IOException(
                        "予約workerの送信失敗後の通信路は再送に使用できません。", writeFailure);
                }
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    await SchedulerWorkerProtocol.WriteAsync(stream, message, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // 書込み量を確定できないため、次のフレームを追記しません。
                    writeFailure = error;
                    throw;
                }
            }
            finally
            {
                writer.Release();
            }
        }
        catch (OperationCanceledException error)
            when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException(
                "予約workerの送信待機または送信が10秒で完了しませんでした。", error);
        }
    }

    internal Task<SchedulerWorkerMessage> ReadAsync(CancellationToken token = default) =>
        SchedulerWorkerProtocol.ReadAsync(stream, identity, token);

    internal async Task RunHeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SendAsync(new SchedulerWorkerMessage.Heartbeat(identity), token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public void Dispose() => writer.Dispose();
}
