using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

[ComImport]
[ComVisible(true)]
[Guid("9b3b0b86-266a-4aad-b21f-fde5501001b7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensorManagerEvents
{
    [PreserveSig]
    int OnSensorEnter(nint sensor, int state);
}

[ComImport]
[ComVisible(true)]
[Guid("5d8dcc91-4641-47e7-b7c3-b74f48a6c391")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISensorEvents
{
    [PreserveSig]
    int OnStateChanged(nint sensor, int state);

    [PreserveSig]
    int OnDataUpdated(nint sensor, nint report);

    [PreserveSig]
    int OnEvent(nint sensor, in Guid eventId, nint data);

    [PreserveSig]
    int OnLeave(in Guid id);
}

/// <summary>
/// 通知の例外をHRESULTへ戻し、終了後の通知を無効化します。
/// </summary>
internal abstract class SensorCallbackSink(SensorCallbackFailure? observed = null)
    : StandardOleMarshalObject
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private Exception? failure;
    private int status;

    internal abstract Type Contract
    {
        get;
    }

    internal void Close()
    {
        Interlocked.Exchange(ref status, 2);
        CloseCore();
    }

    internal void BeginConnect()
    {
        VerifyAccess();
        if (Interlocked.CompareExchange(ref status, 1, 0) != 0)
        {
            throw new InvalidOperationException("通知sinkは接続ごとに作成してください。");
        }
    }

    internal void VerifyOpen() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref status) == 2, this);

    protected abstract void CloseCore();

    internal Exception? TakeFailure() => Interlocked.Exchange(ref failure, null);

    internal void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new COMException("センサー通知は所有COM threadで処理してください。",
                unchecked((int)0x8001010e));
        }
    }

    protected int Capture(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        observed?.Record(error);
        return error.HResult < 0 ? error.HResult : unchecked((int)0x80004005);
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class SensorManagerEventSink(Action<nint, int> callback,
    SensorCallbackFailure? observed = null)
    : SensorCallbackSink(observed), ISensorManagerEvents
{
    private Action<nint, int>? callback = callback;

    internal override Type Contract => typeof(ISensorManagerEvents);

    protected override void CloseCore() => Interlocked.Exchange(ref callback, null);

    public int OnSensorEnter(nint sensor, int state)
    {
        Action<nint, int>? active = Volatile.Read(ref callback);
        if (active is null)
        {
            return 0;
        }

        try
        {
            VerifyAccess();
            active(sensor, state);
            return 0;
        }
        catch (Exception error)
        {
            return Capture(error);
        }
    }
}

/// <summary>
/// 通知中だけ有効な参照を所有threadで受け取ります。
/// </summary>
internal interface ISensorEventReceiver
{
    void StateChanged(nint sensor, int state);

    void DataUpdated(nint sensor, nint report);

    void Event(nint sensor, in Guid eventId, nint data);

    void Leave(in Guid id);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class SensorEventSink(ISensorEventReceiver receiver,
    SensorCallbackFailure? observed = null)
    : SensorCallbackSink(observed), ISensorEvents
{
    private ISensorEventReceiver? receiver = receiver;

    internal override Type Contract => typeof(ISensorEvents);

    protected override void CloseCore() => Interlocked.Exchange(ref receiver, null);

    public int OnStateChanged(nint sensor, int state)
    {
        ISensorEventReceiver? active = Volatile.Read(ref receiver);
        if (active is null)
        {
            return 0;
        }

        try
        {
            VerifyAccess();
            active.StateChanged(sensor, state);
            return 0;
        }
        catch (Exception error)
        {
            return Capture(error);
        }
    }

    public int OnDataUpdated(nint sensor, nint report)
    {
        ISensorEventReceiver? active = Volatile.Read(ref receiver);
        if (active is null)
        {
            return 0;
        }

        try
        {
            VerifyAccess();
            active.DataUpdated(sensor, report);
            return 0;
        }
        catch (Exception error)
        {
            return Capture(error);
        }
    }

    public int OnEvent(nint sensor, in Guid eventId, nint data)
    {
        ISensorEventReceiver? active = Volatile.Read(ref receiver);
        if (active is null)
        {
            return 0;
        }

        try
        {
            VerifyAccess();
            active.Event(sensor, in eventId, data);
            return 0;
        }
        catch (Exception error)
        {
            return Capture(error);
        }
    }

    public int OnLeave(in Guid id)
    {
        ISensorEventReceiver? active = Volatile.Read(ref receiver);
        if (active is null)
        {
            return 0;
        }

        try
        {
            VerifyAccess();
            active.Leave(in id);
            return 0;
        }
        catch (Exception error)
        {
            return Capture(error);
        }
    }
}

/// <summary>
/// 接続が外れた後も、最初の通知失敗を一度だけ受け取ります。
/// </summary>
internal sealed class SensorCallbackFailure
{
    private Exception? failure;

    internal void Record(Exception error) =>
        Interlocked.CompareExchange(ref failure, error, null);

    internal Exception? Take() => Interlocked.Exchange(ref failure, null);
}
