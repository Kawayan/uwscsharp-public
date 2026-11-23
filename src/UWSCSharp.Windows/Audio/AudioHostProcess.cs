using System.IO.Pipes;

namespace UWSCSharp.Windows;

/// <summary>
/// 1回の再生用の子プロセス・通信・Jobを所有します。
/// </summary>
internal sealed class AudioHostProcess : IAudioPlaybackOperation
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    internal const string HostArgument = "--uwsc-audio-host";
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken callerToken;
    private readonly NamedPipeServerStream pipe;
    private readonly Func<WindowsCommandHost, string, CapturedProcess> launch;
    private readonly TaskCompletionSource<uint> launched = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AudioHostMessage> started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AudioHostMessage> completed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? stopping;
    private bool disposed;
    private int cleanupConfirmed;

    private AudioHostProcess(WindowsCommandHost host, AudioHostPlayRequest request,
        CancellationToken cancellationToken,
        Func<WindowsCommandHost, string, CapturedProcess> launch)
    {
        this.launch = launch;
        callerToken = cancellationToken;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string name = "UWSCSharp-audio-" + Guid.NewGuid().ToString("N");
        try
        {
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous
                    | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }

        Closed = Task.Run(() => RunAsync(host, request, name));
    }

    internal Task<uint> Launched => launched.Task;

    internal Task<AudioHostMessage> Started => started.Task;

    internal Task<AudioHostMessage> Completed => completed.Task;

    internal Task Closed
    {
        get;
    }

    internal bool CleanupConfirmed => Volatile.Read(ref cleanupConfirmed) != 0;

    AudioPlaybackSignals IAudioPlaybackOperation.Signals
        => new(Launched, Started, Completed, Closed);

    Task IAudioPlaybackOperation.FinishAsync(CancellationToken token)
        => Closed.WaitAsync(CloseTimeout, token);

    internal static AudioHostProcess Start(WindowsCommandHost host,
        AudioHostPlayRequest request, CancellationToken cancellationToken = default,
        Func<WindowsCommandHost, string, CapturedProcess>? launch = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        AudioHostProtocol.Validate(new(AudioHostMessageType.Play, request));
        ValidatePath(host.Executable);
        if (host.ManagedAssembly is not null)
        {
            ValidatePath(host.ManagedAssembly);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(host, request, cancellationToken, launch ?? LaunchNative);
    }

    internal Task StopAsync()
    {
        lock (gate)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Closed.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    lifetime.Dispose();
                }
            }
        }
    }

    private async Task RunAsync(WindowsCommandHost host,
        AudioHostPlayRequest request, string name)
    {
        CapturedProcess? process = null;
        Exception? primary = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            process = launch(host, name);
            launched.TrySetResult(process.Id);
            await ConnectAsync(process, lifetime.Token).ConfigureAwait(false);
            await AudioHostProtocol.WriteAsync(pipe,
                new(AudioHostMessageType.Play, request), lifetime.Token).ConfigureAwait(false);
            var conversation = new AudioHostConversation();
            while (await AudioHostProtocol.ReadAsync(pipe, lifetime.Token)
                .ConfigureAwait(false) is { } message)
            {
                conversation.Accept(message);
                started.TrySetResult(message);
                if (conversation.IsTerminal)
                {
                    completed.TrySetResult(message);
                }
            }

            conversation.CompleteRead();
            uint exit = await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            if (exit != 0 && (await completed.Task.ConfigureAwait(false)).Type
                != AudioHostMessageType.Failed)
            {
                throw new InvalidDataException($"音声子プロセスが終了コード{exit}で終了しました。");
            }
        }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
        {
            CancellationToken token = callerToken.IsCancellationRequested
                ? callerToken : lifetime.Token;
            launched.TrySetCanceled(token);
            started.TrySetCanceled(token);
            completed.TrySetCanceled(token);
            if (error.Data.Count != 0)
            {
                primary = new InvalidOperationException(
                    "音声起動の取消に解放情報が付随しています。", error);
                throw primary;
            }
        }
        catch (Exception error)
        {
            primary = error;
            launched.TrySetException(error);
            started.TrySetException(error);
            completed.TrySetException(error);
            throw;
        }
        finally
        {
            List<Exception> errors = [];
            try
            {
                pipe.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            if (process is not null)
            {
                var cleanup = new InvalidOperationException("音声子プロセスの停止と解放に失敗しました。");
                try
                {
                    await process.StopAfterFailureAsync(cleanup).ConfigureAwait(false);
                    if (cleanup.Data.Count != 0)
                    {
                        errors.Add(cleanup);
                    }
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }

            if (errors.Count == 0 && process is not null)
            {
                Volatile.Write(ref cleanupConfirmed, 1);
            }
            else if (errors.Count != 0)
            {
                var cleanup = new AggregateException("音声子プロセスの解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["音声子プロセスの解放"] = cleanup;
                }
                else
                {
                    throw cleanup;
                }
            }
        }
    }

    private async Task ConnectAsync(CapturedProcess process, CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task connected = pipe.WaitForConnectionAsync(waiting.Token);
        Task<uint> exited = process.WaitForExitAsync(waiting.Token);
        try
        {
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                uint code = await exited.ConfigureAwait(false);
                throw new EndOfStreamException($"音声子プロセスが接続前に終了しました（{code}）。");
            }

            await connected.ConfigureAwait(false);
        }
        finally
        {
            await waiting.CancelAsync().ConfigureAwait(false);
            await ObserveCancellationAsync(connected).ConfigureAwait(false);
            await ObserveCancellationAsync(exited).ConfigureAwait(false);
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)
            || path.Contains('\0') || path.Contains('"'))
        {
            throw new ArgumentException("音声子プロセスの実行元には絶対パスを指定してください。");
        }
    }

    internal static CapturedProcess LaunchNative(WindowsCommandHost host, string name)
    {
        string prefix = $"\"{host.Executable}\"";
        if (host.ManagedAssembly is not null)
        {
            prefix += $" \"{host.ManagedAssembly}\"";
        }

        string command = $"{prefix} {HostArgument} {name}";
        // 起動途中で取消を返さず、所有権を受け取ってからJobを停止します。
        return CapturedProcess.StartUncaptured(host.Executable,
            command, false, CancellationToken.None);
    }
}
