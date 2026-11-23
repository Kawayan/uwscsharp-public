using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 自分で確保した通知元とCCWを呼び、機器・COM server生成・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorEventNativeApiTests
{
    private static readonly ConcurrentDictionary<nint, Fixture> Instances = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeSourceRegistersTheCorrectContractAndCallsItsComSlots(bool manager)
    {
        using var fixture = new Fixture(manager);
        var receiver = new Receiver();
        int enteredState = 0;
        nint enteredSensor = 0;
        SensorCallbackSink sink = manager
            ? new SensorManagerEventSink((sensor, state) =>
            {
                enteredSensor = sensor;
                enteredState = state;
            })
            : new SensorEventSink(receiver);
        using var connection = SensorEventConnection.Connect(fixture.Source, sink);
        Assert.Equal(0, fixture.QueryResult);
        Assert.Equal(2, fixture.References);
        Assert.Equal(manager ? 6 : 17, fixture.LastSlot);
        if (manager)
        {
            Assert.Equal(0, fixture.Enter(0x101, -123));
            Assert.Equal(0x101, enteredSensor);
            Assert.Equal(-123, enteredState);
        }
        else
        {
            Assert.Equal(0, fixture.State(0x101, -123));
            Assert.Equal(0, fixture.Data(0x101, 0x202));
            Assert.Equal(0, fixture.Event(0x101, fixture.Id, 0x303));
            Assert.Equal(0, fixture.Leave(fixture.Id));
            Assert.Equal(["state:257:-123", "data:257:514",
                $"event:257:{fixture.Id}:771", $"leave:{fixture.Id}"], receiver.Calls);
        }

        connection.Dispose();
        Assert.Equal(0, fixture.Installed);
        Assert.Equal(1, fixture.References);
        Assert.All(fixture.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Fact]
    public void CcwReturnsCallbackHResultAndTheConnectionRetainsTheException()
    {
        using var fixture = new Fixture(true);
        var failure = new COMException("通知中断", unchecked((int)0x80070005));
        var sink = new SensorManagerEventSink((_, _) => throw failure);
        using var connection = SensorEventConnection.Connect(fixture.Source, sink);
        Assert.Equal(failure.HResult, fixture.Enter(0x101, 0));
        Assert.Same(failure, connection.TakeCallbackFailure());
        Assert.Null(connection.TakeCallbackFailure());
    }

    [Fact]
    public void NullNativeSourceIsRejectedBeforeReadingTheVtable()
    {
        Assert.Throws<ArgumentException>(() => SensorEventNativeApi.Instance.SetSink(0, 0, true));
        Assert.Throws<ArgumentException>(() => SensorEventNativeApi.Instance.SetSink(0, 0, false));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint source)
    {
        Fixture fixture = Instances[source];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        return (uint)++fixture.References;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint source)
    {
        Fixture fixture = Instances[source];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        return (uint)--fixture.References;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetManager(nint source, nint sink) => Instances[source].Set(sink, 6);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetSensor(nint source, nint sink) => Instances[source].Set(sink, 17);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int WrongSlot(nint source, nint sink) => unchecked((int)0x80004001);

    private sealed class Receiver : ISensorEventReceiver
    {
        internal List<string> Calls { get; } = [];

        public void StateChanged(nint sensor, int state) => Calls.Add($"state:{sensor}:{state}");

        public void DataUpdated(nint sensor, nint report) => Calls.Add($"data:{sensor}:{report}");

        public void Event(nint sensor, in Guid eventId, nint data) => Calls.Add($"event:{sensor}:{eventId}:{data}");

        public void Leave(in Guid id) => Calls.Add($"leave:{id}");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly nint Source;
        internal readonly Guid Id = new("11223344-5566-7788-99aa-bbccddeeff00");
        internal int References = 1;
        internal nint Installed;
        internal int QueryResult;
        internal int LastSlot;
        internal List<int> Threads { get; } = [];
        private readonly nint table;
        private readonly Guid contract;

        internal unsafe Fixture(bool manager)
        {
            contract = manager ? typeof(ISensorManagerEvents).GUID : typeof(ISensorEvents).GUID;
            table = Marshal.AllocHGlobal(18 * IntPtr.Size);
            Source = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Source, table);
            for (int slot = 0; slot < 18; slot++)
            {
                Marshal.WriteIntPtr(table, slot * IntPtr.Size,
                    (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&WrongSlot);
            }

            Marshal.WriteIntPtr(table, IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef);
            Marshal.WriteIntPtr(table, 2 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release);
            Marshal.WriteIntPtr(table, 6 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&SetManager);
            Marshal.WriteIntPtr(table, 17 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&SetSensor);
            Instances.TryAdd(Source, this);
        }

        internal int Set(nint sink, int slot)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            LastSlot = slot;
            if (sink != 0)
            {
                QueryResult = Marshal.QueryInterface(sink, in contract, out nint queried);
                if (queried != 0)
                {
                    Marshal.Release(queried);
                }

                if (QueryResult < 0)
                {
                    return QueryResult;
                }

                Marshal.AddRef(sink);
            }

            nint previous = Installed;
            Installed = sink;
            if (previous != 0)
            {
                Marshal.Release(previous);
            }

            return 0;
        }

        private nint Slot(int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(Installed), index * IntPtr.Size);

        internal unsafe int Enter(nint sensor, int state) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Slot(3))(Installed, sensor, state);

        internal unsafe int State(nint sensor, int state) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Slot(3))(Installed, sensor, state);

        internal unsafe int Data(nint sensor, nint report) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(4))(Installed, sensor, report);

        internal unsafe int Event(nint sensor, Guid eventId, nint data) =>
            ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint, int>)Slot(5))(Installed, sensor, &eventId, data);

        internal unsafe int Leave(Guid id) =>
            ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(6))(Installed, &id);

        public void Dispose()
        {
            if (Installed != 0)
            {
                Marshal.Release(Installed);
                Installed = 0;
            }

            Instances.TryRemove(Source, out _);
            Marshal.FreeHGlobal(Source);
            Marshal.FreeHGlobal(table);
        }
    }
}
