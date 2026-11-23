using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 再生要求の世代を管理し、前の解放後に次を起動します。
/// </summary>
internal sealed class AudioPlaybackOwner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly object identity = new();
    private readonly Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation> factory;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken runToken;
    private Task tail = Task.CompletedTask;
    private Entry? current;
    private Task? disposal;
    private bool closing;
    private long generation;

    internal AudioPlaybackOwner(WindowsCommandHost host, CancellationToken runToken = default)
        : this(CreateFactory(host), runToken)
    {
    }

    internal AudioPlaybackOwner(
        Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation> factory,
        CancellationToken runToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
        this.runToken = runToken;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(runToken);
    }

    internal AudioPlaybackTicket Replace(AudioHostPlayRequest request)
    {
        AudioHostProtocol.Validate(new(AudioHostMessageType.Play, request));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            runToken.ThrowIfCancellationRequested();
            long nextGeneration = checked(generation + 1);
            Task stop = current?.RequestStopAsync() ?? Task.CompletedTask;
            var entry = new Entry(identity, nextGeneration, AwaitAllAsync(tail, stop),
                factory, request, lifetime.Token, runToken);
            generation = nextGeneration;
            current = entry;
            tail = entry.Ticket.Closed;
            return entry.Ticket;
        }
    }

    internal Task StopAsync()
    {
        lock (gate)
        {
            if (closing)
            {
                return disposal!;
            }

            return StopCurrentLocked();
        }
    }

    internal Task ReleaseAsync(AudioPlaybackTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ReferenceEquals(ticket.OwnerIdentity, identity))
        {
            throw new ArgumentException("別の所有者の再生要求は解放できません。", nameof(ticket));
        }

        lock (gate)
        {
            return !closing && ReferenceEquals(current?.Ticket, ticket)
                ? StopCurrentLocked() : ticket.Closed;
        }
    }

    private Task StopCurrentLocked()
    {
        if (current is not null)
        {
            tail = AwaitAllAsync(tail, current.RequestStopAsync());
            current = null;
        }

        return tail;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closing = true;
            return new(disposal ??= DisposeCoreAsync(tail));
        }
    }

    private async Task DisposeCoreAsync(Task pending)
    {
        try
        {
            await AwaitAllAsync(lifetime.CancelAsync(), pending).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private static Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation>
        CreateFactory(WindowsCommandHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return (request, token) => AudioHostProcess.Start(host, request, token);
    }

    private static async Task AwaitAllAsync(params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);
        try
        {
            await all.ConfigureAwait(false);
        }
        catch
        {
            if (all.Exception is not null)
            {
                throw all.Exception;
            }

            throw;
        }
    }

    private sealed class Entry
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken runToken;
        private readonly TaskCompletionSource<uint> launched = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AudioHostMessage> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AudioHostMessage> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? stopping;
        private bool disposed;

        internal Entry(object identity, long generation, Task predecessor,
            Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation> factory,
            AudioHostPlayRequest request, CancellationToken ownerToken, CancellationToken runToken)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
            this.runToken = runToken;
            var closed = Task.Run(() => RunAsync(predecessor, factory, request));
            Ticket = new(generation, launched.Task, started.Task, completed.Task, closed)
            {
                OwnerIdentity = identity
            };
        }

        internal AudioPlaybackTicket Ticket
        {
            get;
        }

        internal Task RequestStopAsync()
        {
            lock (gate)
            {
                return disposed ? Task.CompletedTask
                    : stopping ??= cancellation.CancelAsync();
            }
        }

        private CancellationToken CancelToken => runToken.IsCancellationRequested
            ? runToken : cancellation.Token;

        private async Task RunAsync(Task predecessor,
            Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation> factory,
            AudioHostPlayRequest request)
        {
            IAudioPlaybackOperation? operation = null;
            AudioPlaybackSignals? signals = null;
            Task[] forwarding = [];
            Exception? primary = null;
            bool cancelled = false;
            try
            {
                // 待機中の要求を取り消しても、前の資源の解放は待ちます。
                await predecessor.ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                operation = factory(request, cancellation.Token);
                signals = operation.Signals;
                forwarding = [ForwardAsync(signals.Launched, launched),
                    ForwardAsync(signals.Started, started), ForwardAsync(signals.Completed, completed)];
                await Task.WhenAny(signals.Closed, signals.Completed)
                    .WaitAsync(cancellation.Token).ConfigureAwait(false);
                if (signals.Closed.IsCompleted)
                {
                    await signals.Closed.ConfigureAwait(false);
                }
                else
                {
                    // 終端通知は保持し、正常な終了の確認後に解放します。
                    await signals.Completed.ConfigureAwait(false);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                if (!signals.Launched.IsCompletedSuccessfully
                    || !signals.Started.IsCompletedSuccessfully
                    || !signals.Completed.IsCompletedSuccessfully)
                {
                    throw new InvalidDataException("再生の解放と通知の状態が一致しません。");
                }

                await Task.WhenAll(forwarding).ConfigureAwait(false);
                await operation.FinishAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
                when (cancellation.IsCancellationRequested && error.Data.Count == 0)
            {
                cancelled = true;
            }
            catch (Exception error)
            {
                primary = error is OperationCanceledException
                    ? new InvalidOperationException("再生処理が予期せず取消を返しました。", error)
                    : error;
            }

            if (signals is not null)
            {
                Task[] notifications = [signals.Launched, signals.Started, signals.Completed];
                for (int index = 0; index < notifications.Length; index++)
                {
                    if (notifications[index].IsCompleted)
                    {
                        await forwarding[index].ConfigureAwait(false);
                    }
                }
            }

            if (primary is not null)
            {
                launched.TrySetException(primary);
                started.TrySetException(primary);
                completed.TrySetException(primary);
            }

            List<Exception> errors = [];
            try
            {
                await RequestStopAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                AddError(error);
            }

            if (operation is not null)
            {
                try
                {
                    await operation.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    AddError(error);
                }

                if (signals is not null)
                {
                    foreach (Task notification in new Task[]
                        { signals.Launched, signals.Started, signals.Completed })
                    {
                        if (notification.Exception is { } failure)
                        {
                            foreach (Exception error in failure.InnerExceptions)
                            {
                                AddError(error);
                            }
                        }
                    }

                    if (!signals.Closed.IsCompleted)
                    {
                        AddError(new InvalidDataException("再生の解放後もClosedが完了していません。"));
                    }
                    else
                    {
                        try
                        {
                            await signals.Closed.ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            AddError(error);
                        }
                    }
                }
            }

            await Task.WhenAll(forwarding).ConfigureAwait(false);
            CancellationToken cancelledToken = CancelToken;
            lock (gate)
            {
                disposed = true;
                cancellation.Dispose();
            }

            if (errors.Count != 0)
            {
                var cleanup = new AggregateException("再生の解放に失敗しました。", errors);
                if (primary is null)
                {
                    primary = cleanup;
                }
                else
                {
                    primary.Data["再生の解放"] = cleanup;
                }
            }

            if (primary is not null)
            {
                launched.TrySetException(primary);
                started.TrySetException(primary);
                completed.TrySetException(primary);
                ExceptionDispatchInfo.Capture(primary).Throw();
            }

            if (cancelled)
            {
                launched.TrySetCanceled(cancelledToken);
                started.TrySetCanceled(cancelledToken);
                completed.TrySetCanceled(cancelledToken);
            }

            void AddError(Exception error)
            {
                if (!ReferenceEquals(primary, error) && !errors.Any(item => ReferenceEquals(item, error)))
                {
                    errors.Add(error);
                }
            }
        }

        private async Task ForwardAsync<T>(Task<T> source, TaskCompletionSource<T> target)
        {
            try
            {
                target.TrySetResult(await source.WaitAsync(cancellation.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                target.TrySetCanceled(CancelToken);
            }
            catch (Exception error)
            {
                target.TrySetException(error);
            }
        }
    }
}
