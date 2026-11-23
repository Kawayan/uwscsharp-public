using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace UWSCSharp.Windows;

/// <summary>
/// 32ビット中継を起動し、同じ接続を使う呼出しの応答を要求番号で振り分けます。
/// </summary>
internal sealed class NativeDllRelayClient : IDisposable
{
    private readonly object gate = new();
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly Process process;
    private readonly Dictionary<long, TaskCompletionSource<NativeDllRelayReply>> pending = [];
    private readonly Task receiving;
    private long lastId;
    private Exception? connectionError;
    private bool disposed;

    internal NativeDllRelayClient(NamedPipeServerStream pipe,
        Process process)
    {
        this.pipe = pipe;
        this.process = process;
        reader = new StreamReader(pipe, leaveOpen: true);
        receiving = ReceiveAsync();
    }

    internal static async Task<NativeDllRelayClient> StartAsync(
        string runtime, string host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = $"uwsc-dll-relay-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(runtime)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(name);
        Process? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("DLL中継を開始できませんでした。");
            Task connect = pipe.WaitForConnectionAsync(deadline.Token);
            Task exit = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connect, exit).ConfigureAwait(false) == exit)
            {
                await exit.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"DLL中継が接続前に終了しました: {process.ExitCode}");
            }

            await connect.ConfigureAwait(false);
            return new NativeDllRelayClient(pipe, process);
        }
        catch
        {
            pipe.Dispose();
            if (process is not null)
            {
                Stop(process);
            }

            throw;
        }
        finally
        {
            deadline.Cancel();
        }
    }

    internal NativeDllRelayReply Invoke(NativeDllRelayRequest request,
        CancellationToken token)
    {
        var completion = new TaskCompletionSource<NativeDllRelayReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (connectionError is not null)
            {
                throw new InvalidOperationException("DLL中継との通信が終了しました。", connectionError);
            }

            long id = ++lastId;
            byte[] message = JsonSerializer.SerializeToUtf8Bytes(request with
            {
                Id = id
            });
            token.ThrowIfCancellationRequested();
            pending.Add(id, completion);
            try
            {
                pipe.Write(message);
                pipe.WriteByte((byte)'\n');
                pipe.Flush();
            }
            catch (IOException error)
            {
                FailConnection(error);
            }
        }

        // 送信済みの呼出しは、DLLが返す書戻しと共に完了させます。
        return completion.Task.GetAwaiter().GetResult();
    }

    internal void CloseWorker(long workerId)
    {
        try
        {
            Invoke(new("", null, WorkerId: workerId), CancellationToken.None);
        }
        catch (InvalidOperationException error) when (error.InnerException is not null)
        {
            // 切断済みの中継は終了処理で回収します。
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                NativeDllRelayReply reply = JsonSerializer.Deserialize<NativeDllRelayReply>(line)
                    ?? throw new InvalidDataException("DLL中継の応答が空です。");
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    if (!pending.Remove(reply.Id, out TaskCompletionSource<NativeDllRelayReply>? completion))
                    {
                        throw new InvalidDataException("DLL中継の応答番号が一致しません。");
                    }

                    completion.SetResult(reply);
                }
            }

            throw new EndOfStreamException("DLL中継から応答がありません。");
        }
        catch (Exception error) when (error is IOException or JsonException
            or InvalidDataException or ObjectDisposedException)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    FailConnection(error);
                }
            }
        }
    }

    private void FailConnection(Exception error)
    {
        connectionError = error;
        pipe.Dispose();
        foreach (TaskCompletionSource<NativeDllRelayReply> completion in pending.Values)
        {
            completion.SetException(new InvalidOperationException(
                "DLL中継との通信が終了しました。", error));
        }

        pending.Clear();
    }

    /// <summary>
    /// 接続を閉じてDLLの解放を待ち、終了しない子だけを停止します。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            FailConnection(new ObjectDisposedException(nameof(NativeDllRelayClient)));
        }

        receiving.GetAwaiter().GetResult();
        reader.Dispose();
        Stop(process);
    }

    private static void Stop(Process process)
    {
        using (process)
        {
            if (process.WaitForExit(5000))
            {
                return;
            }

            try
            {
                process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }

            process.WaitForExit(5000);
        }
    }
}
