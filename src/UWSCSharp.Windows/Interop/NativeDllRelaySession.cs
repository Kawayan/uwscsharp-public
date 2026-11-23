using System.Collections.Concurrent;
using System.Text.Json;

namespace UWSCSharp.Windows;

internal sealed record NativeDllRelayRequest(string Library, NativeDllRelayCall? Call,
    long Id = 0, long WorkerId = 0);

internal sealed record NativeDllRelayReply(NativeDllRelayResult? Result,
    string? ErrorType = null, string? ErrorMessage = null, long Id = 0);

/// <summary>
/// 中継プロセス内でDLLを所有し、切断時に解放します。
/// </summary>
internal sealed class NativeDllRelaySession : IDisposable
{
    private readonly object moduleGate = new();
    private readonly Dictionary<string, NativeDllModule> modules = new(StringComparer.Ordinal);
    private bool disposed;

    internal void Serve(Stream connection)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var reader = new StreamReader(connection, leaveOpen: true);
        object writeGate = new();
        List<Task> running = [];
        Dictionary<long, RelayWorker> workers = [];
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                NativeDllRelayRequest request = JsonSerializer.Deserialize<NativeDllRelayRequest>(line)
                    ?? throw new InvalidDataException("DLL中継の要求が空です。");
                running.RemoveAll(task => task.IsCompletedSuccessfully);
                if (request.WorkerId == 0)
                {
                    running.Add(Task.Run(() => ReplyOrDisconnect(request)));
                    continue;
                }

                if (request.Call is null)
                {
                    if (workers.Remove(request.WorkerId, out RelayWorker? closing))
                    {
                        closing.Enqueue(request);
                        running.Add(closing.CompleteAsync());
                    }
                    else
                    {
                        ReplyOrDisconnect(request);
                    }

                    continue;
                }

                if (!workers.TryGetValue(request.WorkerId, out RelayWorker? worker))
                {
                    worker = new RelayWorker(ReplyOrDisconnect);
                    workers.Add(request.WorkerId, worker);
                }

                worker.Enqueue(request);
            }
        }
        finally
        {
            foreach (RelayWorker worker in workers.Values)
            {
                running.Add(worker.CompleteAsync());
            }

            Task.WhenAll(running).GetAwaiter().GetResult();
        }

        void ReplyOrDisconnect(NativeDllRelayRequest request)
        {
            try
            {
                Reply(request);
            }
            catch
            {
                // 応答を返せない場合は、親が待ち続けないよう接続を閉じます。
                connection.Dispose();
                throw;
            }
        }

        void Reply(NativeDllRelayRequest request)
        {
            NativeDllRelayReply reply;
            try
            {
                reply = request.Call is null && request.WorkerId != 0
                    ? new(null, Id: request.Id)
                    : new(Invoke(request), Id: request.Id);
            }
            catch (Exception error) when (error is ArgumentException
                or IOException or UnauthorizedAccessException or DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException
                or InvalidOperationException or OverflowException)
            {
                reply = new(null, error.GetType().Name, error.Message, request.Id);
            }

            lock (writeGate)
            {
                try
                {
                    connection.Write(JsonSerializer.SerializeToUtf8Bytes(reply));
                    connection.WriteByte((byte)'\n');
                    connection.Flush();
                }
                catch (IOException)
                {
                    // 親が切断した場合も、実行中のDLLを解放まで保持します。
                }
            }
        }
    }

    private sealed class RelayWorker
    {
        private readonly BlockingCollection<NativeDllRelayRequest> requests = new();

        internal RelayWorker(Action<NativeDllRelayRequest> reply)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Completion = completion.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    foreach (NativeDllRelayRequest request in requests.GetConsumingEnumerable())
                    {
                        reply(request);
                    }

                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            })
            {
                IsBackground = true
            };
            thread.Start();
        }

        private Task Completion
        {
            get;
        }

        internal void Enqueue(NativeDllRelayRequest request) => requests.Add(request);

        internal async Task CompleteAsync()
        {
            requests.CompleteAdding();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            finally
            {
                requests.Dispose();
            }
        }
    }

    private NativeDllRelayResult Invoke(NativeDllRelayRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.Library);
        ArgumentNullException.ThrowIfNull(request.Call);
        if (!Path.IsPathFullyQualified(request.Library))
        {
            throw new ArgumentException("解決済みのDLLの絶対パスを指定してください。");
        }

        string path = Path.GetFullPath(request.Library);
        NativeDllModule module;
        lock (moduleGate)
        {
            if (!modules.TryGetValue(path, out module!))
            {
                module = NativeDllModule.LoadX86RelayLibrary(path);
                modules.Add(path, module);
            }
        }

        return NativeDllRelayInvocation.Invoke(module, request.Call);
    }

    /// <summary>
    /// このプロセスが読み込んだDLLを解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (NativeDllModule module in modules.Values)
        {
            module.Dispose();
        }

        modules.Clear();
    }
}
