using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 所有する子プロセス内だけでCOMイベントの接続と解除を確認します。
/// </summary>
internal static class ComEventConnectionProbe
{
    internal static async Task<int> RunAsync(string progId,
        bool frameworkThread = false, bool active = false)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            return 90;
        }

        try
        {
            Console.Error.WriteLine($"event-probe-pid={Environment.ProcessId}");
            var runtime = new Runtime(active);
            int calls = 0;
            int otherCalls = 0;
            int reentrantCalls = 0;
            int callbackThread = 0;
            var session = new ComSession(runtime);
            bool propagated = false;
            Exception? primary = null;
            try
            {
                Value target = await session.CreateAsync(progId, false, default);
                IComEventConnection connection = await session.ConnectValueEventAsync(
                    target, "ieventprobeevents", "changed", Changed, default);
                Check(await Number("ListenerCount") == 1, "最初の接続件数が異なります。");
                Check(await Number("Fire", [Value.Numeric(42)]) == 1,
                    "取消引数が同期的に反映されません。");
                Check(calls == 1, "同期通知の件数が異なります。");
                Check(connection.TakeCallbackFailure() is null, "通知が失敗しました。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Console.Error.WriteLine($"event-probe-stage=before-async,framework={frameworkThread}");
                if (frameworkThread)
                {
                    await session.InvokeAsync(target, "BeginFire",
                        [Value.Numeric(42)], null, default);
                    Console.Error.WriteLine("event-probe-stage=after-begin-async");
                    while (await Number("AsyncDone") == 0)
                    {
                        await Task.Delay(10, timeout.Token);
                    }

                    Check(await Number("AsyncError") == 0
                        && await Number("AsyncResult") == 1,
                        "Frameworkスレッドの発火と取消引数の反映が失敗しました。");
                }
                else
                {
                    Check(await runtime.FireFromMtaAsync().WaitAsync(timeout.Token) == 1,
                        "MTAからの取消引数が反映されません。");
                }

                if (connection.TakeCallbackFailure() is { } callbackError)
                {
                    throw new InvalidOperationException(
                        "別スレッドの発火で通知が失敗しました。", callbackError);
                }

                Check(calls == 2, "別スレッドの通知件数が異なります。");
                await MustFail("missing-interface", "Changed");
                await MustFail("IEventProbeEvents", "missing-event");
                Check(await Number("ListenerCount") == 1,
                    "接続失敗によって既存の登録が変わりました。");
                await session.InvokeAsync(target, "FireOther",
                    [Value.String("未登録")], null, default);
                Check(calls == 2, "別のDISPIDの通知が配送されました。");
                await session.ConnectValueEventAsync(target, "IEventProbeEvents",
                    "Other", Other, default);
                await session.InvokeAsync(target, "FireOther",
                    [Value.String("日本語")], null, default);
                Check(otherCalls == 1, "別イベントが届きません。");
                Check(await session.DisconnectEventsAsync(target, default) == 2,
                    "全イベントが解除されません。");
                Check(await session.DisconnectEventsAsync(target, default) == 0,
                    "重複解除の件数が異なります。");
                Check(await Number("ListenerCount") == 0,
                    "接続点が残っています。");
                Check(await Number("Fire", [Value.Numeric(42)]) == 0 && calls == 2,
                    "解除後にも通知されました。");
                var expected = new InvalidOperationException("callback-fixture");
                IComEventConnection failed = await session.ConnectValueEventAsync(
                    target, "IEventProbeEvents", "Changed",
                    async (values, token) =>
                    {
                        await Task.Yield();
                        throw expected;
                    }, default);
                try
                {
                    await Number("Fire", [Value.Numeric(42)]);
                }
                catch (Exception)
                {
                    propagated = true;
                }

                Check(ReferenceEquals(expected, failed.TakeCallbackFailure()),
                    "コールバックの元例外が保持されません。");
                Check(failed.TakeCallbackFailure() is null,
                    "同じコールバック失敗が重複して取り出されました。");

                async Task<double> Number(string name, IReadOnlyList<Value>? arguments = null)
                    => (await session.InvokeAsync(target, name,
                        arguments ?? [], null, default)).AsNumber();

                async Task MustFail(string interfaceName, string eventName)
                {
                    Exception? failure = null;
                    try
                    {
                        await session.ConnectEventAsync(target, interfaceName,
                            eventName, (ref DISPPARAMS parameters) => { }, default);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }

                    Check(failure is not null
                        && session.GetScriptErrorMessage(failure) is not null,
                        "不明なイベントがCOM操作失敗として識別されません。");
                }

                async ValueTask<IReadOnlyList<Value>> Changed(
                    IReadOnlyList<Value> values, CancellationToken token)
                {
                    Console.Error.WriteLine($"event-probe-callback={Environment.CurrentManagedThreadId}");
                    Check(callbackThread == 0
                        || callbackThread == Environment.CurrentManagedThreadId,
                        $"COM通知のSTAが変わりました: {callbackThread}→{Environment.CurrentManagedThreadId}");
                    callbackThread = Environment.CurrentManagedThreadId;
                    calls++;
                    Check(values.Count == 2 && values[0].AsNumber() == 42,
                        "通知引数の数または順序が異なります。");
                    Check(values[1].NativeReferenceType == 0x400b,
                        "BOOL参照引数の型が異なります。");
                    await ReenterAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
                    return [Value.Numeric(999), Value.Logical(true)];
                }

                async Task ReenterAsync()
                {
                    await Task.Yield();
                    Check(await Number("ListenerCount") == 1,
                        "同期通知中のCOM再呼出しが失敗しました。");
                    reentrantCalls++;
                }

                ValueTask<IReadOnlyList<Value>> Other(
                    IReadOnlyList<Value> values, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    Check(values.Count == 1, "文字列イベントの引数数が異なります。");
                    Check(values[0].Display() == "日本語", "文字列イベントの値が異なります。");
                    Check(callbackThread == Environment.CurrentManagedThreadId,
                        "COM通知のSTAが変わりました。");
                    otherCalls++;
                    return ValueTask.FromResult<IReadOnlyList<Value>>(
                        [Value.String("値渡しの変更")]);
                }
            }
            catch (Exception failure)
            {
                primary = failure;
                throw;
            }
            finally
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    primary.Data["UWSCSharp.ComEventProbeCleanup"] = cleanup;
                    Console.Error.WriteLine(cleanup);
                }
            }

            Check(runtime.Releases == 1 && runtime.ListenersAtRelease == 0,
                "COM参照の解放前にイベントが解除されませんでした。");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                calls,
                otherCalls,
                reentrantCalls,
                callbackThread,
                propagated,
                releases = runtime.Releases,
                listenersAtRelease = runtime.ListenersAtRelease
            }));
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Runtime(bool active) : IComRuntime
    {
        private readonly NativeComRuntime native = new();
        private object? instance;
        internal int Releases;
        internal int ListenersAtRelease = -1;

        public object Create(string name) => instance = active
            ? native.GetActive(name) : native.Create(name);

        internal Task<int> FireFromMtaAsync() => Task.Run(() =>
        {
            object? result = native.Invoke(instance!, "Fire", [42], false);
            object? scalar = result is ComVariantValue typed ? typed.Value : result;
            return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        });

        public object GetActive(string name) => native.GetActive(name);

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign) =>
            native.Invoke(target, name, arguments, assign);

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token) =>
            native.ConnectEvent(target, interfaceName, eventName, callback, token);

        public void Release(object target)
        {
            try
            {
                object? result = native.Invoke(target, "ListenerCount", [], false);
                object? scalar = result is ComVariantValue typed ? typed.Value : result;
                ListenersAtRelease = Convert.ToInt32(scalar,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                Releases++;
                native.Release(target);
            }
        }
    }
}
