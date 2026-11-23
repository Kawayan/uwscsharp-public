using System.Collections.Concurrent;

namespace UWSCSharp.Windows;

internal interface ISensorSessionController : IDisposable
{
    int Refresh();

    bool TryRead(SensorDataField field, out object? value);

    Exception? TakeCallbackFailure();
}

/// <summary>
/// COM参照を含めず、取得状態とコピー済みの値を返します。
/// </summary>
internal readonly record struct SensorQueryResult(int DiscoveryResult, bool HasValue, object? Value);

/// <summary>
/// センサーの生成・要求・通知・解放を一つのSTAへ配送します。
/// </summary>
internal sealed class SensorSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly BlockingCollection<ICommand> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ISensorSessionController> createController;
    private readonly ISensorApartment apartment;
    private readonly int ownerThread;
    private ISensorSessionController? controller;
    private bool disposed;

    internal SensorSession(Func<ISensorSessionController>? createController = null,
        ISensorApartment? apartment = null)
    {
        this.createController = createController ?? (() => new SensorController());
        this.apartment = apartment ?? SensorApartment.Instance;
        var thread = new Thread(Run) { IsBackground = true, Name = "UWSC Sensors" };
        ownerThread = thread.ManagedThreadId;
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            commands.Dispose();
            throw;
        }
    }

    internal ValueTask<SensorQueryResult> QueryAsync(SensorDataField field,
        CancellationToken token = default) => QueryCoreAsync(field, token);

    internal ValueTask<SensorQueryResult> QueryAsync(int selector,
        CancellationToken token = default) => QueryCoreAsync(
            SensorDataFieldCatalog.TryGet(selector, out SensorDataField field) ? field : null,
            token);

    private ValueTask<SensorQueryResult> QueryCoreAsync(SensorDataField? field,
        CancellationToken token) => Submit(active =>
    {
        int result = active.Refresh();
        token.ThrowIfCancellationRequested();
        object? value = null;
        bool found = field is SensorDataField selected && active.TryRead(selected, out value);
        if (found && value is not (double or long or bool or string))
        {
            throw new InvalidOperationException("センサー値はコピー済みのscalarか文字列が必要です。");
        }

        return new SensorQueryResult(result, found, found ? value : null);
    }, token);

    internal ValueTask<Exception?> TakeCallbackFailureAsync(CancellationToken token = default) =>
        Submit(active => active.TakeCallbackFailure(), token);

    private ValueTask<T> Submit<T>(Func<ISensorSessionController, T> action,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            // 起動失敗はreadyから返し、終了状態で元の例外を隠しません。
            ObjectDisposedException.ThrowIf(disposed && !ready.Task.IsFaulted, this);
        }

        // 通知から所有threadへ再入した要求を、自分のqueue待ちにしません。
        if (Environment.CurrentManagedThreadId == ownerThread && !ready.Task.IsFaulted)
        {
            ISensorSessionController active = controller
                ?? throw new InvalidOperationException("センサーの初期化が完了していません。");
            T value = action(active);
            token.ThrowIfCancellationRequested();
            return new ValueTask<T>(value);
        }

        return new ValueTask<T>(EnqueueAsync(action, token));
    }

    private async Task<T> EnqueueAsync<T>(Func<ISensorSessionController, T> action,
        CancellationToken token)
    {
        await ready.Task.ConfigureAwait(false);
        var command = new Command<T>(action, token);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            commands.Add(command);
        }

        return await command.Result.ConfigureAwait(false);
    }

    private void Run()
    {
        bool initialized = false;
        Exception? terminal = null;
        var errors = new List<Exception>();
        try
        {
            SensorNativeErrors.ThrowForResult(apartment.Initialize());
            initialized = true;
            bool create;
            lock (gate)
            {
                create = !disposed;
            }

            if (create)
            {
                controller = createController()
                    ?? throw new InvalidOperationException("センサーcontrollerがNULLです。");
            }

            ready.TrySetResult();
            while (true)
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        break;
                    }
                }

                if (commands.TryTake(out ICommand? command, 10))
                {
                    bool begin;
                    lock (gate)
                    {
                        begin = !disposed;
                    }

                    if (!begin)
                    {
                        command.Fail(new ObjectDisposedException(nameof(SensorSession)));
                        break;
                    }

                    command.Run(controller!);
                }

                lock (gate)
                {
                    if (disposed)
                    {
                        break;
                    }
                }

                apartment.Pump();
            }
        }
        catch (Exception error)
        {
            terminal = error;
            ready.TrySetException(error);
            errors.Add(error);
        }
        finally
        {
            lock (gate)
            {
                disposed = true;
                commands.CompleteAdding();
            }

            while (commands.TryTake(out ICommand? pending))
            {
                pending.Fail(terminal ?? new ObjectDisposedException(nameof(SensorSession)));
            }

            try
            {
                controller?.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            controller = null;
            if (initialized)
            {
                try
                {
                    apartment.Uninitialize();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            commands.Dispose();
            if (errors.Count == 0)
            {
                completed.TrySetResult();
            }
            else
            {
                completed.TrySetException(new AggregateException("センサーSTAの終了に失敗しました。", errors));
            }
        }
    }

    /// <summary>
    /// 開始済みの同期呼出しを待ち、未開始の要求を実行せずに終了します。
    /// </summary>
    /// <remarks>所有threadの通知内では終了を要求し、通知から戻った後に完了を待ちます。</remarks>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                commands.CompleteAdding();
            }
        }

        return new ValueTask(completed.Task);
    }

    private interface ICommand
    {
        void Run(ISensorSessionController active);

        void Fail(Exception error);
    }

    private sealed class Command<T>(Func<ISensorSessionController, T> action,
        CancellationToken token) : ICommand
    {
        private readonly TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<T> Result => result.Task;

        public void Run(ISensorSessionController active)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                T value = action(active);
                // 同期COM呼出しが戻り、一時参照を解放した後に取消を公開します。
                token.ThrowIfCancellationRequested();
                result.TrySetResult(value);
            }
            catch (OperationCanceledException error) when (error.CancellationToken == token)
            {
                result.TrySetCanceled(token);
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
        }

        public void Fail(Exception error) => result.TrySetException(error);
    }
}
