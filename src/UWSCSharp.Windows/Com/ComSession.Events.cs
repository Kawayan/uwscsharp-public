using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 接続した通知経路の解除と失敗の取出しを提供します。
/// </summary>
internal interface IComEventConnection : IDisposable
{
    Exception? TakeCallbackFailure();
}

internal sealed partial class ComSession
{
    private sealed record EventEntry(object Target, IComEventConnection Connection,
        object? Owner = null, string? InterfaceName = null, string? EventName = null);

    private readonly List<EventEntry>
        eventConnections = [];

    /// <summary>
    /// 引数を値へコピーし、非同期の通知処理後に参照引数を反映します。
    /// </summary>
    internal ValueTask<IComEventConnection> ConnectValueEventAsync(Value target,
        string interfaceName, string eventName,
        Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>> callback,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ConnectEventAsync(target, interfaceName, eventName,
            CreateValueEventHandler(callback, token), token);
    }

    private NativeComEventHandler CreateValueEventHandler(
        Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>> callback, CancellationToken token) =>
            (ref DISPPARAMS parameters) =>
            {
                token.ThrowIfCancellationRequested();
                var frame = new NativeComEventArguments(in parameters);
                var readBudget = new ComValueBudget();
                var inputBudget = new ComValueBudget();
                var values = new Value[frame.Count];
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = Wrap(frame.Read(index, token, readBudget),
                        budget: inputBudget, token: token);
                }

                Task<IReadOnlyList<Value>> pending = callback(
                    Array.AsReadOnly(values), token).AsTask();
                WaitForEventCallback(pending);
                IReadOnlyList<Value> result = pending.GetAwaiter().GetResult();
                ArgumentNullException.ThrowIfNull(result);
                if (result.Count != frame.Count)
                {
                    throw new InvalidOperationException(
                        "COMイベントの引数数は変更できません。");
                }

                var outputBudget = new ComValueBudget();
                object?[] output = new object?[result.Count];
                for (int index = 0; index < result.Count; index++)
                {
                    if (frame.IsByReference(index))
                    {
                        output[index] = Unwrap(result[index], outputBudget);
                    }
                }

                frame.WriteBack(output, token);
            };

    internal ValueTask SetScriptEventAsync(object owner, Value target,
        string? interfaceName, string? eventName,
        Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>>? callback,
        CancellationToken callbackToken, CancellationToken token) => new(
        SubmitAsync(() =>
        {
            object instance = Resolve(target);
            if (callback is null)
            {
                return DisconnectMatching(entry => ReferenceEquals(entry.Target, instance));
            }

            ArgumentException.ThrowIfNullOrEmpty(interfaceName);
            ArgumentException.ThrowIfNullOrEmpty(eventName);
            IComEventConnection connection = RegisterEvent(instance, interfaceName,
                eventName, CreateValueEventHandler(callback, callbackToken), token,
                owner);
            return DisconnectMatching(entry => entry.Owner is not null
                && !ReferenceEquals(entry.Connection, connection)
                && ReferenceEquals(entry.Target, instance)
                && string.Equals(entry.InterfaceName, interfaceName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.EventName, eventName,
                    StringComparison.OrdinalIgnoreCase));
        }, token).AsTask());

    internal ValueTask DisconnectEventOwnerAsync(object owner) => new(
        SubmitAsync(() => DisconnectMatching(entry => ReferenceEquals(entry.Owner, owner)),
            CancellationToken.None).AsTask());

    /// <summary>
    /// 同期通知が完了するまで、再呼出しとSTAの通知を処理します。
    /// </summary>
    internal void WaitForEventCallback(Task callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Environment.CurrentManagedThreadId != staThreadId)
        {
            throw new InvalidOperationException(
                "COM通知の待機は接続したSTAで実行してください。");
        }

        // 借用中の引数を守るため、取消時も通知本体の終了を待ちます。
        while (!callback.IsCompleted)
        {
            if (commands.TryTake(out Action? action, 10))
            {
                action();
            }

            while (Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }

            if (commands.IsCompleted)
            {
                Thread.Sleep(1);
            }
        }

        callback.GetAwaiter().GetResult();
    }

    internal ValueTask<IComEventConnection> ConnectEventAsync(Value target,
        string interfaceName, string eventName, NativeComEventHandler callback,
        CancellationToken token) => SubmitAsync(() =>
        {
            object instance = Resolve(target);
            return RegisterEvent(instance, interfaceName, eventName, callback, token);
        }, token);

    private IComEventConnection RegisterEvent(object instance,
        string interfaceName, string eventName, NativeComEventHandler callback,
        CancellationToken token, object? owner = null)
    {
        IComEventConnection connection = runtime.ConnectEvent(
            instance, interfaceName, eventName, callback, token);
        try
        {
            eventConnections.Add(new EventEntry(instance, connection,
                owner, interfaceName, eventName));
            return connection;
        }
        catch (Exception primary)
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception cleanup)
            {
                primary.Data["UWSCSharp.ComEventCleanupErrors"] = cleanup;
            }

            throw;
        }
    }

    internal ValueTask<int> DisconnectEventsAsync(Value target,
        CancellationToken token) => SubmitAsync(() =>
        {
            object instance = Resolve(target);
            return DisconnectMatching(entry => ReferenceEquals(entry.Target, instance));
        }, token);

    private int DisconnectMatching(Func<EventEntry, bool> accepts)
    {
        EventEntry[] selected = [.. eventConnections.Where(accepts).Reverse()];
        var removing = new HashSet<EventEntry>(selected,
            ReferenceEqualityComparer.Instance);
        // 解除中の再入が、外側の対象や新しい登録を変更しないようにします。
        eventConnections.RemoveAll(removing.Contains);
        List<Exception> errors = [];
        int removed = 0;
        foreach (EventEntry entry in selected)
        {
            try
            {
                entry.Connection.Dispose();
                removed++;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        if (errors.Count > 1)
        {
            var failure = new AggregateException(
                "COMイベントの解除に失敗しました。", errors);
            if (errors.All(ComOperationErrors.Contains))
            {
                ComOperationErrors.Record(failure);
            }

            throw failure;
        }

        return removed;
    }

    private void CloseEvents(List<Exception> errors)
    {
        for (int index = eventConnections.Count - 1; index >= 0; index--)
        {
            try
            {
                eventConnections[index].Connection.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        eventConnections.Clear();
    }
}
