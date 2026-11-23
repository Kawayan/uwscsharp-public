using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal interface IComRuntime
{
    object Create(string name);

    object GetActive(string name);

    object? Invoke(object target, string name, object?[] arguments, bool assign);

    object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference)
    {
        if (byReference.Any(value => value))
        {
            throw new NotSupportedException("COMのVAR引数には対応していません。");
        }

        return Invoke(target, name, arguments, assign);
    }

    void Release(object target);

    IComEventConnection ConnectEvent(object target, string interfaceName,
        string eventName, NativeComEventHandler callback,
        CancellationToken token) => throw new NotSupportedException(
            "このCOM実行先はイベント接続に対応していません。");

    object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference, IReadOnlyList<string?> argumentNames)
    {
        if (argumentNames.Any(value => value is not null))
        {
            throw new NotSupportedException("COMの名前付き引数には対応していません。");
        }

        return Invoke(target, name, arguments, assign, byReference);
    }

    object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference, IReadOnlyList<string?> argumentNames,
        ushort? controlWord) =>
        Invoke(target, name, arguments, assign, byReference, argumentNames);
}

[SupportedOSPlatform("windows")]
internal sealed partial class NativeComRuntime : IComRuntime
{
    public object Create(string name)
    {
        try
        {
            return Activator.CreateInstance(
                Type.GetTypeFromProgID(name, throwOnError: true)!)!;
        }
        catch (COMException failure)
        {
            ComOperationErrors.Record(failure);
            throw;
        }
    }

    public object GetActive(string name)
    {
        ComOperationErrors.ThrowIfFailed(CLSIDFromProgID(name, out Guid id));
        ComOperationErrors.ThrowIfFailed(GetActiveObject(ref id, 0, out object value));
        return value;
    }

    public object? Invoke(object target, string name, object?[] arguments, bool assign)
        => Invoke(target, name, arguments, assign, []);

    public object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference)
        => Invoke(target, name, arguments, assign, byReference, []);

    public object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference, IReadOnlyList<string?> argumentNames) =>
        Invoke(target, name, arguments, assign, byReference, argumentNames, null);

    public object? Invoke(object target, string name, object?[] arguments, bool assign,
        IReadOnlyList<bool> byReference, IReadOnlyList<string?> argumentNames,
        ushort? controlWord)
    {
        int[] order = [.. Enumerable.Range(0, arguments.Length).OrderBy(index =>
            index < argumentNames.Count && argumentNames[index] is not null ? 0 : 1)];
        object?[] arranged = [.. order.Select(index => arguments[index])];
        string[] names = [.. order.Where(index => index < argumentNames.Count
            && argumentNames[index] is not null).Select(index => argumentNames[index]!)];
        try
        {
            bool[] arrangedReferences = [.. order.Select(index =>
                index < byReference.Count && byReference[index])];
            return NativeComDispatch.Invoke(target, name, arranged, arrangedReferences,
                names, (uint)CultureInfo.CurrentCulture.LCID, assign,
                controlWord: controlWord);
        }
        catch (Exception ex)
        {
            if (ex.Data["UWSCSharp.ComArgumentIndex"] is int index
                && index >= 0 && index < order.Length)
            {
                ex.Data["UWSCSharp.ComArgumentIndex"] = order[index];
            }

            throw;
        }
        finally
        {
            // 失敗時の返却参照も追跡できるよう、元の引数順へ戻します。
            for (int i = 0; i < order.Length; i++)
            {
                arguments[order[i]] = arranged[i];
            }
        }
    }

    public void Release(object target)
    {
        if (Marshal.IsComObject(target))
        {
            Marshal.ReleaseComObject(target);
        }
    }

    public IComEventConnection ConnectEvent(object target, string interfaceName,
        string eventName, NativeComEventHandler callback, CancellationToken token) =>
        NativeComEventConnection.Connect(target, interfaceName, eventName, callback, token);
}

/// <summary>
/// COM参照の生成から解放までを実行専用STA内に閉じ込めます。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ComSession : IAsyncDisposable
{
    private sealed record Reference(ComSession Owner, object Instance);

    private readonly IComRuntime runtime;
    private readonly int staThreadId;
    private readonly BlockingCollection<Action> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<object, Reference> references = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> acquisitions = [];
    private readonly object gate = new();
    private readonly ConditionalWeakTable<Exception, object> scriptErrors = new();
    private bool disposed;

    internal string? GetScriptErrorMessage(Exception failure) =>
        scriptErrors.TryGetValue(failure, out _) ? failure.Message : null;

    internal ComSession(IComRuntime? runtime = null)
    {
        this.runtime = runtime ?? new NativeComRuntime();
        var thread = new Thread(Run) { IsBackground = true, Name = "UWSC COM" };
        staThreadId = thread.ManagedThreadId;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal ValueTask<Value> CreateAsync(string name, bool active, CancellationToken token) =>
        SubmitAsync(() => Wrap(active ? runtime.GetActive(name) : runtime.Create(name)), token);

    /// <summary>
    /// 実行専用STAで配列を作成し、管理対象の値へコピーします。
    /// </summary>
    internal ValueTask<Value> CreateSafeArrayAsync(SafeArrayRequest request,
        CancellationToken token) => SubmitAsync(() =>
    {
        Value result = Wrap(ComSafeArray.Create(
            request.ElementType, [.. request.LowerBounds],
            [.. request.UpperBounds], token), token: token);
        if (request.ElementType == 12)
        {
            return result;
        }

        UwscArray array = result.AsArray();
        Value initial = array.Count > 0 ? array.Values[0]
            : Wrap(ComSafeArray.Create(request.ElementType, [0], [0], token),
                token: token).AsArray().Get(0);
        return result with
        {
            Array = array.WithElementConversion(initial, (value, operationToken) =>
                ConvertArrayElement(value, request.ElementType, operationToken))
        };
    }, token);

    private Value ConvertArrayElement(Value value, ushort type,
        CancellationToken token)
    {
        Value Convert()
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Value result = Wrap(ComSafeArray.ConvertElement(Unwrap(value), type),
                    token: token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception ex) when (ComOperationErrors.Contains(ex))
            {
                // 値変換だけをscriptの値エラーへ変え、元のHRESULTも保持します。
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        if (Environment.CurrentManagedThreadId == staThreadId)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            return Convert();
        }

        // 作成時のtokenを捕捉せず、更新を行う実行のtokenでSTAへ渡します。
        return SubmitAsync(Convert, token).AsTask().GetAwaiter().GetResult();
    }

    internal ValueTask<Value> InvokeAsync(Value target, string name,
        IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token,
        ushort? controlWord = null) => SubmitAsync(() =>
        {
            object instance = Resolve(target);
            var budget = new ComValueBudget();
            object?[] converted = [.. arguments.Select(value => Unwrap(value, budget))];
            if (assigned is Value value)
            {
                converted = [.. converted, Unwrap(value, budget)];
            }

            return Wrap(controlWord.HasValue
                ? runtime.Invoke(instance, name, converted, assigned.HasValue,
                    [], [], controlWord)
                : runtime.Invoke(instance, name, converted, assigned.HasValue));
        }, token);

    internal ValueTask<WindowsMemberResult> InvokeWithReferencesAsync(Value target,
        string name, IReadOnlyList<WindowsMemberArgument> arguments, Value? assigned,
        CancellationToken token, ushort? controlWord = null) => SubmitAsync(() =>
        {
            object instance = Resolve(target);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WindowsMemberArgument argument in arguments)
            {
                if (argument.Name is not null
                    && (argument.Name.Length == 0 || !names.Add(argument.Name)))
                {
                    throw ComOperationErrors.Record(
                        new InvalidOperationException("COM引数名が空または重複しています。"));
                }
            }

            var budget = new ComValueBudget();
            object?[] converted = [.. arguments.Select(argument => argument.ByReference
                ? new VariantWrapper(Unwrap(argument.Value, budget))
                : Unwrap(argument.Value, budget))];
            bool[] byReference = [.. arguments.Select(argument => argument.ByReference)];
            string?[] argumentNames = [.. arguments.Select(argument => argument.Name)];
            if (assigned is Value value)
            {
                converted = [.. converted, Unwrap(value, budget)];
                byReference = [.. byReference, false];
                argumentNames = [.. argumentNames, null];
            }

            object?[] inputs = [.. converted];
            object? returned = null;
            Exception? primary = null;
            var outputErrors = new List<Exception>();
            try
            {
                returned = runtime.Invoke(instance, name, converted, assigned.HasValue,
                    byReference, argumentNames, controlWord);
            }
            catch (Exception ex)
            {
                primary = ex;
            }

            var outputBudget = new ComValueBudget();
            Value result = default;
            try
            {
                result = Wrap(returned, budget: outputBudget);
            }
            catch (Exception ex)
            {
                outputErrors.Add(ex);
            }

            Value[] outputs = [.. arguments.Select(argument => argument.Value)];
            for (int i = 0; i < arguments.Count; i++)
            {
                if (!byReference[i] || ReferenceEquals(converted[i], inputs[i]))
                {
                    continue;
                }

                try
                {
                    // 失敗・取消で公開しない返却参照もSTA内で所有し、終了時に解放します。
                    outputs[i] = Wrap(converted[i] is VariantWrapper wrapper
                        ? wrapper.WrappedObject : converted[i], budget: outputBudget);
                }
                catch (Exception ex)
                {
                    outputErrors.Add(ex);
                }
            }

            if (primary is not null)
            {
                if (outputErrors.Count > 0)
                {
                    primary.Data["UWSCSharp.ComOutputErrors"] = new AggregateException(outputErrors);
                }

                ExceptionDispatchInfo.Capture(primary).Throw();
            }

            if (outputErrors.Count > 0)
            {
                var failure = new AggregateException("COMの返却値変換に失敗しました。", outputErrors);
                if (outputErrors.All(ComOperationErrors.Contains))
                {
                    ComOperationErrors.Record(failure);
                }

                throw failure;
            }

            return new WindowsMemberResult(result, Array.AsReadOnly(outputs));
        }, token);

    internal ValueTask<IReadOnlyList<Value>> EnumerateAsync(Value target, CancellationToken token) =>
        SubmitAsync<IReadOnlyList<Value>>(() =>
        {
            if (target.Kind == ValueKind.Array)
            {
                return System.Array.AsReadOnly(target.AsArray().Values.ToArray());
            }

            object instance = Resolve(target);
            if (!Marshal.IsComObject(instance) && instance is not IEnumerable)
            {
                throw ComOperationErrors.Record(
                    new InvalidOperationException("COM objectは列挙可能ではありません。"));
            }

            var values = new List<Value>();
            var budget = new ComValueBudget();
            IEnumerator enumerator = Marshal.IsComObject(instance)
                ? NativeComEnumerator.Open(instance)
                : ((IEnumerable)instance).GetEnumerator();
            Exception? primary = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    token.ThrowIfCancellationRequested();
                    if (values.Count == UwscArray.MaximumElements)
                    {
                        throw ComOperationErrors.Record(
                            new InvalidOperationException("コレクションの要素数が上限を超えました。"));
                    }

                    budget.Reserve(1);
                    values.Add(Wrap(enumerator.Current, budget: budget));
                }
            }
            catch (Exception ex)
            {
                primary = ex;
                throw;
            }
            finally
            {
                var errors = new List<Exception>();
                try
                {
                    (enumerator as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }

                try
                {
                    // CLRの列挙adapterが保持するIEnumVARIANTも同じSTAで解放します。
                    runtime.Release(enumerator is ICustomAdapter adapter
                        ? adapter.GetUnderlyingObject() : enumerator);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }

                if (errors.Count > 0)
                {
                    var cleanup = new AggregateException("COM列挙子の解放に失敗しました。", errors);
                    if (primary is not null)
                    {
                        primary.Data["UWSCSharp.CleanupErrors"] = cleanup;
                    }
                    else
                    {
                        throw cleanup;
                    }
                }
            }

            return values.AsReadOnly();
        }, token);

    private object Resolve(Value value)
    {
        if (value.Kind != ValueKind.Object || value.ObjectReference is not Reference reference
            || !ReferenceEquals(reference.Owner, this))
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("このsessionが所有するCOM objectを指定してください。"));
        }

        return reference.Instance;
    }

    private object? Unwrap(Value value, ComValueBudget? budget = null,
        int depth = 0)
    {
        if (depth > 128)
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("COM配列の入れ子が深すぎます。"));
        }

        object? scalar = ComOperationErrors.Validate(value.GetUnchangedNativeScalar);
        if (scalar is not null && value.NativeVariantType is ushort type)
        {
            return new ComVariantValue(type, scalar);
        }

        switch (value.Kind)
        {
            case ValueKind.Empty:
                return null;
            case ValueKind.Nothing:
                return value.NativeVariantType == 13 ? new UnknownWrapper(null) : new DispatchWrapper(null);
            case ValueKind.Null:
                return DBNull.Value;
            case ValueKind.Omitted:
                return Type.Missing;
            case ValueKind.Error:
                return new ErrorWrapper(value.AsErrorCode());
            case ValueKind.Object:
                return value.NativeVariantType is 9 or 13
                    ? new ComVariantValue(value.NativeVariantType.Value, Resolve(value)) : Resolve(value);
            case ValueKind.String:
                return value.NativeVariantType == 8
                    && value.NativeScalar is null && value.Text.Length == 0
                    ? new ComVariantValue(8, null) : value.Text;
            case ValueKind.Number:
                return value.Number;
            case ValueKind.Array:
                UwscArray array = value.AsArray();
                budget ??= new ComValueBudget();
                budget.Reserve(array.Values.Count);
                return new ComArrayValue(array.NativeElementType ?? 12,
                    [.. array.LowerBounds], [.. array.UpperBounds],
                    Array.AsReadOnly(array.Values.Select(element =>
                        Unwrap(element, budget, depth + 1)).ToArray()));
            default:
                throw ComOperationErrors.Record(
                    new InvalidOperationException("COMへ渡せない値です。"));
        }
    }

    private Value Wrap(object? value, int depth = 0,
        bool alreadyOwned = false, ComValueBudget? budget = null,
        CancellationToken token = default)
    {
        budget ??= new ComValueBudget();
        if (depth > 128)
        {
            throw ComOperationErrors.Record(
                new InvalidOperationException("COM配列の入れ子が深すぎます。"));
        }

        switch (value)
        {
            case ComVariantValue typed:
                if (typed.Acquisitions is not null)
                {
                    acquisitions.AddRange(typed.Acquisitions);
                    alreadyOwned = true;
                }

                Value wrapped = typed.Type == 0 ? Value.Empty
                    : typed.Type == 8 && typed.Value is null ? Value.String("")
                    : typed.Type is 9 or 13 && typed.Value is not null ? WrapObject(typed.Value, alreadyOwned)
                    : typed.Value is DateTime date ? ComOperationErrors.Validate(
                        () => Value.Numeric(date.ToOADate()))
                    : Wrap(typed.Value, depth, alreadyOwned, budget, token);
                return wrapped with
                {
                    NativeVariantType = typed.Type,
                    NativeReferenceType = typed.ReferenceType,
                    NativeScalar = wrapped.Kind is ValueKind.Number or ValueKind.String
                        ? typed.Value : null
                };
            case ComArrayValue array:
                budget.Reserve(array.Values.Count);
                var elements = new List<Value>();
                foreach (object? element in array.Values)
                {
                    token.ThrowIfCancellationRequested();
                    elements.Add(Wrap(element, depth + 1, alreadyOwned, budget, token));
                }

                return ComOperationErrors.Validate(() => Value.FromArray(
                    UwscArray.CreateBounded(array.LowerBounds, array.UpperBounds,
                        elements).WithNativeElementType(array.ElementType)));
            case null:
                return Value.Nothing;
            case DBNull:
                return new(ValueKind.Null);
            case string text:
                return Value.String(text);
            case bool flag:
                return Value.Logical(flag);
            case ErrorWrapper error:
                return Value.FromError(error.ErrorCode);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return ComOperationErrors.Validate(() => Value.Numeric(number));
            case Array array:
                if (array.LongLength > UwscArray.MaximumElements)
                {
                    throw ComOperationErrors.Record(
                        new InvalidOperationException("COM配列の要素数が上限を超えました。"));
                }

                budget.Reserve(array.Length);
                var values = new List<Value>();
                foreach (object? element in array)
                {
                    token.ThrowIfCancellationRequested();
                    values.Add(Wrap(element, depth + 1, alreadyOwned, budget, token));
                }

                int[] lower = [.. Enumerable.Range(0, array.Rank).Select(array.GetLowerBound)];
                int[] upper = [.. Enumerable.Range(0, array.Rank).Select(array.GetUpperBound)];
                return ComOperationErrors.Validate(() =>
                    Value.FromArray(UwscArray.CreateBounded(lower, upper, values)));
            default:
                return WrapObject(value, alreadyOwned);
        }
    }

    private Value WrapObject(object value, bool alreadyOwned)
    {
        // native読取りで取得済みの参照を二重に所有登録しません。
        if (!alreadyOwned)
        {
            acquisitions.Add(value);
        }

        if (!references.TryGetValue(value, out Reference? reference))
        {
            reference = new Reference(this, value);
            references.Add(value, reference);
        }

        return Value.FromObject(reference);
    }

    private async ValueTask<T> SubmitAsync<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await ready.Task.ConfigureAwait(false);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            commands.Add(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    T value = action();
                    // 同期COM呼出しの完了を待ってから取消を公開します。
                    token.ThrowIfCancellationRequested();
                    result.SetResult(value);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == token)
                {
                    result.SetCanceled(token);
                }
                catch (Exception ex)
                {
                    if (ComOperationErrors.Contains(ex))
                    {
                        scriptErrors.GetValue(ex, _ => new object());
                    }

                    result.SetException(ex);
                }
            });
        }

        return await result.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        bool initialized = false;
        var errors = new List<Exception>();
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
            initialized = true;
            ready.SetResult();
            while (!commands.IsCompleted)
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
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            errors.Add(ex);
        }
        finally
        {
            CloseEvents(errors);
            for (int i = acquisitions.Count - 1; i >= 0; i--)
            {
                try
                {
                    runtime.Release(acquisitions[i]);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            acquisitions.Clear();
            references.Clear();
            if (initialized)
            {
                CoUninitialize();
            }

            if (errors.Count == 0)
            {
                completed.SetResult();
            }
            else
            {
                completed.SetException(new AggregateException("COM sessionの解放に失敗しました。", errors));
            }
        }
    }

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
}
