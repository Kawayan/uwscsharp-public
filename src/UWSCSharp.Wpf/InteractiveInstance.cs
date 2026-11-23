using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace UWSCSharp.Wpf;

internal sealed class InteractiveInstance : IDisposable
{
    private const int MaximumPathBytes = 128 * 1024;
    internal sealed record RunRequest(string Path, string[] Arguments);
    private readonly string name;
    private readonly Mutex lease;
    private readonly CancellationTokenSource stopping = new();

    internal InteractiveInstance()
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        name = $"UWSCSharp.Wpf.{user.User!.Value}.{process.SessionId}";
        lease = new Mutex(false, @"Local\" + name, out bool first);
        IsFirst = first;
    }

    internal bool IsFirst
    {
        get;
    }

    internal async Task<bool> RequestOpenAsync(string? path, RunRequest? run = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", name,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        byte[] payload = run is null ? Encoding.UTF8.GetBytes(path ?? "")
            : JsonSerializer.SerializeToUtf8Bytes(run);
        if (payload.Length > MaximumPathBytes)
        {
            throw new IOException("転送する起動要求が大きすぎます。");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header,
            run is null ? payload.Length : ~payload.Length);
        await client.WriteAsync(header, timeout.Token);
        await client.WriteAsync(payload, timeout.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        byte[] reply = new byte[1];
        await client.ReadExactlyAsync(reply, timeout.Token);
        return reply[0] == 1;
    }

    internal async Task ListenAsync(Dispatcher dispatcher, Func<string?, bool> open,
        Func<RunRequest, bool> run)
    {
        CancellationToken token = stopping.Token;
        using var server = new NamedPipeServerStream(name,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                | PipeOptions.FirstPipeInstance);
        while (!token.IsCancellationRequested)
        {
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            try
            {
                (string? path, RunRequest? request) =
                    await ReadRequestAsync(server, token).ConfigureAwait(false);
                bool opened = await dispatcher.InvokeAsync(
                    () => request is null ? open(path) : run(request),
                    DispatcherPriority.Normal, token);
                await server.WriteAsync(new byte[] { opened ? (byte)1 : (byte)0 }, token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                // 呼び出し元が終了しても、次の起動を受け付けます。
            }
            finally
            {
                server.Disconnect();
            }
        }
    }

    private static async Task<(string? Path, RunRequest? Run)> ReadRequestAsync(Stream stream,
        CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            bool execute = length < 0;
            if (execute)
            {
                length = ~length;
            }

            if (length > MaximumPathBytes)
            {
                throw new IOException("転送されたファイル名の長さが無効です。");
            }

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            if (!execute)
            {
                return (length == 0 ? null : Encoding.UTF8.GetString(payload), null);
            }

            RunRequest? request = JsonSerializer.Deserialize<RunRequest>(payload);
            if (request?.Path is not { } path || !Path.IsPathFullyQualified(path)
                || !Path.GetExtension(path).Equals(".uws", StringComparison.OrdinalIgnoreCase)
                || request.Arguments is null || Array.IndexOf(request.Arguments, null) >= 0)
            {
                throw new IOException("転送された実行要求が無効です。");
            }

            return (null, request);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new IOException("起動要求の受信が時間切れになりました。");
        }
    }

    /// <summary>
    /// 起動要求の受付を終了し、次回の起動に備えて所有権を解放します。
    /// </summary>
    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
        lease.Dispose();
    }
}
