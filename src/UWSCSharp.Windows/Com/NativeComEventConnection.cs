using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 通知中だけ有効なCOM引数領域を同期的に処理します。
/// </summary>
internal delegate void NativeComEventHandler(ref DISPPARAMS parameters);

/// <summary>
/// 型情報で識別したディスパッチイベントの接続を所有します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeComEventConnection : IComEventConnection
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly Sink sink;
    private readonly int cookie;
    private IConnectionPoint? connection;

    private NativeComEventConnection(IConnectionPoint connection,
        int cookie, Sink sink)
    {
        this.connection = connection;
        this.cookie = cookie;
        this.sink = sink;
    }

    /// <summary>
    /// 最初の通知失敗を一度だけ取り出します。
    /// </summary>
    public Exception? TakeCallbackFailure() => sink.TakeFailure();

    internal static NativeComEventConnection Connect(object target,
        string interfaceName, string eventName,
        NativeComEventHandler callback, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(interfaceName);
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(callback);
        token.ThrowIfCancellationRequested();
        if (target is not IConnectionPointContainer container)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COMイベントの接続点が必要です。"));
        }

        (Guid interfaceId, int memberId) = target is IProvideClassInfo provider
            ? ResolveEvent(provider, interfaceName, eventName, token)
            : ResolveTypeLibraryEvent(target, interfaceName, eventName, token);
        IConnectionPoint? point = null;
        var candidate = new Sink(interfaceId, memberId, callback);
        try
        {
            token.ThrowIfCancellationRequested();
            container.FindConnectionPoint(ref interfaceId, out point);
            if (point is null)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COMイベントの接続点が返されませんでした。"));
            }

            point.GetConnectionInterface(out Guid actualId);
            if (actualId != interfaceId)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COM接続点のインタフェースが一致しません。"));
            }

            point.Advise(candidate, out int cookie);
            return new NativeComEventConnection(point, cookie, candidate);
        }
        catch (Exception primary)
        {
            candidate.Close();
            if (primary is COMException)
            {
                ComOperationErrors.Record(primary);
            }

            Release(point, primary);
            throw;
        }
    }

    private static (Guid InterfaceId, int MemberId) ResolveEvent(
        IProvideClassInfo provider, string interfaceName, string eventName,
        CancellationToken token)
    {
        ITypeInfo? classInfo = null;
        Exception? primary = null;
        try
        {
            ComOperationErrors.ThrowIfFailed(provider.GetClassInfo(out classInfo));
            if (classInfo is null)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COMイベントのクラス型情報が返されませんでした。"));
            }

            int count = unchecked((ushort)ReadTypeAttributes(classInfo).cImplTypes);

            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                IMPLTYPEFLAGS flags = default;
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)classInfo).GetImplTypeFlags(index, out flags));
                if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) == 0)
                {
                    continue;
                }

                int reference = 0;
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)classInfo).GetRefTypeOfImplType(index, out reference));
                ITypeInfo sourceInfo = null!;
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)classInfo).GetRefTypeInfo(reference, out sourceInfo));
                if (sourceInfo is null)
                {
                    throw ComOperationErrors.Record(new InvalidOperationException(
                        "COMイベントのインタフェース型情報が返されませんでした。"));
                }

                Exception? sourceFailure = null;
                try
                {
                    string name = "";
                    ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)sourceInfo).GetDocumentation(-1, out name,
                        out _, out _, out _));
                    if (!string.Equals(name, interfaceName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TYPEATTR sourceAttribute = ReadTypeAttributes(sourceInfo);

                    if (sourceAttribute.typekind != TYPEKIND.TKIND_DISPATCH)
                    {
                        throw ComOperationErrors.Record(new InvalidOperationException(
                            "ディスパッチ型のCOMイベントを指定してください。"));
                    }

                    return (sourceAttribute.guid, ResolveMember(
                        sourceInfo, sourceAttribute, eventName, token));
                }
                catch (Exception failure)
                {
                    sourceFailure = failure;
                    throw;
                }
                finally
                {
                    Release(sourceInfo, sourceFailure);
                }
            }

            throw ComOperationErrors.Record(new InvalidOperationException(
                $"COMイベントのインタフェースがありません: {interfaceName}"));
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            Release(classInfo, primary);
        }
    }

    private static int ResolveMember(ITypeInfo info, TYPEATTR attributes,
        string eventName, CancellationToken token)
    {
        int count = unchecked((ushort)attributes.cFuncs);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            nint description = 0;
            ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)info).GetFuncDesc(index, out description));
            if (description == 0)
            {
                throw ComOperationErrors.Record(new InvalidOperationException(
                    "COMイベントの関数記述が返されませんでした。"));
            }

            Exception? primary = null;
            try
            {
                FUNCDESC function = Marshal.PtrToStructure<FUNCDESC>(description);
                string name = "";
                ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)info).GetDocumentation(function.memid,
                    out name, out _, out _, out _));
                if (function.invkind == INVOKEKIND.INVOKE_FUNC
                    && string.Equals(name, eventName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return function.memid;
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
                    info.ReleaseFuncDesc(description);
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    AddCleanup(primary, cleanup);
                }
            }
        }

        throw ComOperationErrors.Record(new InvalidOperationException(
            $"COMイベントがありません: {eventName}"));
    }

    private static TYPEATTR ReadTypeAttributes(ITypeInfo info)
    {
        nint attribute = 0;
        ComOperationErrors.ThrowIfFailed(((IEventTypeInfo)info).GetTypeAttr(out attribute));
        if (attribute == 0)
        {
            throw ComOperationErrors.Record(new InvalidOperationException(
                "COMイベントの型属性が返されませんでした。"));
        }

        Exception? primary = null;
        try
        {
            return Marshal.PtrToStructure<TYPEATTR>(attribute);
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
                info.ReleaseTypeAttr(attribute);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                AddCleanup(primary, cleanup);
            }
        }
    }

    private static void Operation(Action action)
    {
        try
        {
            action();
        }
        catch (Exception failure) when (failure is COMException or NotImplementedException)
        {
            ComOperationErrors.Record(failure);
            throw;
        }
    }

    /// <summary>
    /// 通知を閉じてから接続点を解除し、参照を解放します。
    /// </summary>
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException(
                "COMイベントの解除は接続したスレッドで実行してください。");
        }

        IConnectionPoint? point = connection;
        if (point is null)
        {
            return;
        }

        connection = null;
        sink.Close();
        Exception? primary = null;
        try
        {
            point.Unadvise(cookie);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is COMException)
            {
                ComOperationErrors.Record(failure);
            }

            throw;
        }
        finally
        {
            Release(point, primary);
        }
    }

    private static void Release(object? value, Exception? primary = null)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
        catch (Exception cleanup) when (primary is not null)
        {
            AddCleanup(primary, cleanup);
        }
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "UWSCSharp.ComEventCleanupErrors";
        IEnumerable<Exception> previous = primary.Data[key]
            is AggregateException aggregate ? aggregate.InnerExceptions : [];
        primary.Data[key] = new AggregateException(previous.Append(cleanup));
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class Sink(Guid interfaceId, int memberId,
        NativeComEventHandler callback) : StandardOleMarshalObject,
        IDispatchSink, ICustomQueryInterface
    {
        private NativeComEventHandler? handler = callback;
        private Exception? failure;

        internal void Close() => Interlocked.Exchange(ref handler, null);

        internal Exception? TakeFailure() => Interlocked.Exchange(ref failure, null);

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint pointer)
        {
            pointer = 0;
            if (iid != interfaceId)
            {
                return CustomQueryInterfaceResult.NotHandled;
            }

            pointer = Marshal.GetComInterfaceForObject(this,
                typeof(IDispatchSink), CustomQueryInterfaceMode.Ignore);
            return CustomQueryInterfaceResult.Handled;
        }

        public int GetTypeInfoCount(out uint count)
        {
            count = 0;
            return 0;
        }

        public int GetTypeInfo(uint index, uint locale, out nint info)
        {
            info = 0;
            return unchecked((int)0x80004001);
        }

        public int GetIDsOfNames(ref Guid iid, nint names,
            uint count, uint locale, nint ids) => unchecked((int)0x80020006);

        public int Invoke(int id, ref Guid iid, uint locale, ushort flags,
            ref DISPPARAMS parameters, nint result, nint exception, nint argumentError)
        {
            if (result != 0)
            {
                Marshal.WriteInt16(result, 0);
            }

            if (iid != Guid.Empty)
            {
                return unchecked((int)0x80020001);
            }

            NativeComEventHandler? active = Volatile.Read(ref handler);
            if (id != memberId || active is null)
            {
                return 0;
            }

            try
            {
                active(ref parameters);
                return 0;
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                return Marshal.GetHRForException(error);
            }
        }
    }
}
