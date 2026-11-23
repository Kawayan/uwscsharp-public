using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace UWSCSharp.Wpf;

/// <summary>
/// 現在の利用者だけが接続できる予約ごとのローカルパイプを所有します。
/// </summary>
internal sealed class SchedulerWorkerPipe : IDisposable
{
    private const string Prefix = "UWSCSharp-schedule-";

    internal string Name { get; } = Prefix + Guid.NewGuid().ToString("N");

    internal NamedPipeServerStream Stream
    {
        get;
    }

    internal SchedulerWorkerPipe()
    {
        Stream = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous
                | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal async Task WaitForConnectionAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(SchedulerWorkerProtocol.CommunicationTimeout);
        await Stream.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(string name,
        CancellationToken token = default)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name.AsSpan(Prefix.Length), "N", out _))
        {
            throw new InvalidDataException("予約workerのパイプ名が不正です。");
        }

        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Anonymous);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(SchedulerWorkerProtocol.CommunicationTimeout);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose() => Stream.Dispose();
}
