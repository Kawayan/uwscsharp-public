using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 実COM初期化とmessage pumpを使うため、Sandbox内だけで実行します。
[Trait("Category", "SandboxOnly")]
public sealed class SensorApartmentIsolationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarshaledCallbacksArriveOnTheSessionSta(bool manager)
    {
        RequireSandbox();
        var apartment = new Apartment();
        var receiver = new Receiver();
        Packet? packet = null;
        int createdThread = 0;
        await using var session = new SensorSession(() =>
        {
            createdThread = Environment.CurrentManagedThreadId;
            SensorCallbackSink sink = manager
                ? new SensorManagerEventSink(receiver.Enter)
                : new SensorEventSink(receiver);
            packet = new Packet(sink);
            return new Backend(() => packet.Dispose());
        }, apartment);
        Assert.Equal(42.5d, (await session.QueryAsync(Field())).Value);
        Assert.NotNull(packet);
        int clientThread = await Task.Run(() => InvokeFromMta(packet, manager))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(createdThread, clientThread);
        Assert.Equal(manager ? 1 : 4, receiver.Threads.Count);
        Assert.All(receiver.Threads, thread => Assert.Equal(createdThread, thread));
        Assert.All(receiver.Apartments, state => Assert.Equal(ApartmentState.STA, state));
        Assert.Equal(manager ? ["enter:0:3"] : new[]
        {
            "state:0:3", "data:0:0", $"event:0:{EventId}:0", $"leave:{EventId}"
        }, receiver.Calls);
        Assert.Null(await session.TakeCallbackFailureAsync());
        await session.DisposeAsync();
        Assert.Equal(createdThread, apartment.InitializedThread);
        Assert.Equal(createdThread, apartment.UninitializedThread);
        Assert.True(apartment.InitializeResult >= 0);
        Assert.Equal(1, apartment.Uninitializes);
        Assert.True(apartment.Dpi > 0);
        Assert.InRange(apartment.Awareness, 0, 2);
        output.WriteLine($"manager={manager}; owner={createdThread}; client={clientThread}; callbacks={receiver.Calls.Count}; init=0x{apartment.InitializeResult:x8}; uninitialize=1; owner-dpi-api={apartment.Dpi}; owner-awareness={apartment.Awareness}");
    }

    [Fact]
    public async Task NativeManagerIsCreatedAndReleasedWithoutRequestingPermissions()
    {
        RequireSandbox();
        var apartment = new Apartment();
        int result = unchecked((int)0x80004005);
        int ownerThread = 0;
        bool created = false;
        await using var session = new SensorSession(() =>
        {
            ownerThread = Environment.CurrentManagedThreadId;
            nint manager = 0;
            try
            {
                result = SensorManagerFactory.Instance.Create(out manager);
                created = manager != 0;
            }
            finally
            {
                if (manager != 0)
                {
                    Marshal.Release(manager);
                }
            }

            return new Backend(() => { });
        }, apartment);
        await session.QueryAsync(Field());
        await session.DisposeAsync();
        output.WriteLine($"manager-activation=0x{result:x8}; nonnull={created}; owner={ownerThread}; uninitialize={apartment.Uninitializes}; permissions=not-called; owner-dpi-api={apartment.Dpi}; owner-awareness={apartment.Awareness}");
        Assert.True(result >= 0, $"manager生成失敗: 0x{result:x8}");
        Assert.True(created);
        Assert.Equal(ownerThread, apartment.UninitializedThread);
        Assert.Equal(1, apartment.Uninitializes);
        Assert.True(apartment.Dpi > 0);
        Assert.InRange(apartment.Awareness, 0, 2);
    }

    private static readonly Guid EventId = new("11223344-5566-7788-99aa-bbccddeeff00");

    private void RequireSandbox()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(Directory.Exists(@"C:\ui-input"));
        uint dpi = GetDpiForSystem();
        int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        output.WriteLine($"guest-user={Environment.UserName}; caller-dpi-api={dpi}; caller-awareness={awareness}");
        Assert.True(dpi > 0);
        Assert.InRange(awareness, 0, 2);
    }

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private static unsafe int InvokeFromMta(Packet packet, bool manager)
    {
        int initialized = CoInitializeEx(0, 0);
        Marshal.ThrowExceptionForHR(initialized, new nint(-1));
        nint proxy = 0;
        try
        {
            nint stream = packet.Take();
            Guid contract = packet.Contract;
            int result = CoGetInterfaceAndReleaseStream(stream, in contract, out proxy);
            Marshal.ThrowExceptionForHR(result, new nint(-1));
            Assert.NotEqual(0, proxy);
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            nint table = Marshal.ReadIntPtr(proxy);
            nint state = Marshal.ReadIntPtr(table, 3 * IntPtr.Size);
            // proxyへはNULLを渡し、架空のセンサー参照をmarshalしません。
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)state)(proxy, 0, 3));
            if (!manager)
            {
                nint data = Marshal.ReadIntPtr(table, 4 * IntPtr.Size);
                nint custom = Marshal.ReadIntPtr(table, 5 * IntPtr.Size);
                nint leave = Marshal.ReadIntPtr(table, 6 * IntPtr.Size);
                Guid id = EventId;
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)data)(proxy, 0, 0));
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint, int>)custom)(proxy, 0, &id, 0));
                Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)leave)(proxy, &id));
            }

            return Environment.CurrentManagedThreadId;
        }
        finally
        {
            if (proxy != 0)
            {
                Marshal.Release(proxy);
            }

            CoUninitialize();
        }
    }

    private sealed class Packet : IDisposable
    {
        private readonly SensorCallbackSink sink;
        private nint stream;
        internal Guid Contract
        {
            get;
        }

        internal Packet(SensorCallbackSink sink)
        {
            this.sink = sink;
            Contract = sink.Contract.GUID;
            nint pointer = SensorEventNativeApi.Instance.GetSinkPointer(sink);
            try
            {
                Guid contract = Contract;
                Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(
                    in contract, pointer, out stream), new nint(-1));
                Assert.NotEqual(0, stream);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        internal nint Take()
        {
            nint transferred = Interlocked.Exchange(ref stream, 0);
            Assert.NotEqual(0, transferred);
            return transferred;
        }

        public unsafe void Dispose()
        {
            sink.Close();
            nint abandoned = Interlocked.Exchange(ref stream, 0);
            if (abandoned != 0)
            {
                try
                {
                    nint table = Marshal.ReadIntPtr(abandoned);
                    nint seek = Marshal.ReadIntPtr(table, 5 * IntPtr.Size);
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, nint, int>)seek)
                        (abandoned, 0, 0, 0), new nint(-1));
                    Marshal.ThrowExceptionForHR(CoReleaseMarshalData(abandoned), new nint(-1));
                }
                finally
                {
                    Marshal.Release(abandoned);
                }
            }

            GC.KeepAlive(sink);
        }
    }

    private sealed class Receiver : ISensorEventReceiver
    {
        internal List<int> Threads { get; } = [];
        internal List<ApartmentState> Apartments { get; } = [];
        internal List<string> Calls { get; } = [];

        internal void Enter(nint sensor, int state) => Record($"enter:{sensor}:{state}");

        public void StateChanged(nint sensor, int state) => Record($"state:{sensor}:{state}");

        public void DataUpdated(nint sensor, nint report) => Record($"data:{sensor}:{report}");

        public void Event(nint sensor, in Guid eventId, nint data) => Record($"event:{sensor}:{eventId}:{data}");

        public void Leave(in Guid id) => Record($"leave:{id}");

        private void Record(string value)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Apartments.Add(Thread.CurrentThread.GetApartmentState());
            Calls.Add(value);
        }
    }

    private sealed class Backend(Action dispose) : ISensorSessionController
    {
        public int Refresh() => 0;

        public bool TryRead(SensorDataField field, out object? value)
        {
            value = 42.5d;
            return true;
        }

        public Exception? TakeCallbackFailure() => null;

        public void Dispose() => dispose();
    }

    private sealed class Apartment : ISensorApartment
    {
        internal int InitializeResult;
        internal int InitializedThread;
        internal int UninitializedThread;
        internal int Uninitializes;
        internal uint Dpi;
        internal int Awareness;

        public int Initialize()
        {
            InitializedThread = Environment.CurrentManagedThreadId;
            Dpi = GetDpiForSystem();
            Awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            InitializeResult = SensorApartment.Instance.Initialize();
            return InitializeResult;
        }

        public void Pump() => SensorApartment.Instance.Pump();

        public void Uninitialize()
        {
            UninitializedThread = Environment.CurrentManagedThreadId;
            Uninitializes++;
            SensorApartment.Instance.Uninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoMarshalInterThreadInterfaceInStream(
        in Guid contract, nint instance, out nint stream);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoGetInterfaceAndReleaseStream(
        nint stream, in Guid contract, out nint instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoReleaseMarshalData(nint stream);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
}
