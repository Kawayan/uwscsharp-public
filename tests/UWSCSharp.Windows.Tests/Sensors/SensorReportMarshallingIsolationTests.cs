using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 非NULL reportのCOM配送とmessage pumpを、Sandbox内だけで確認します。
[Trait("Category", "SandboxOnly")]
public sealed class SensorReportMarshallingIsolationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(64)]
    public async Task NotificationReadsAndCopiesAMarshaledReport(int selector)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(Directory.Exists(@"C:\ui-input"));
        uint callerDpi = GetDpiForSystem();
        int callerAwareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        output.WriteLine($"guest-user={Environment.UserName}; caller-dpi-api={callerDpi}; caller-awareness={callerAwareness}");
        Assert.True(callerDpi > 0);
        Assert.InRange(callerAwareness, 0, 2);
        Assert.True(SensorDataFieldCatalog.TryGet(selector, out SensorDataField field));
        var apartment = new Apartment();
        var receiver = new Receiver(field);
        Packet? packet = null;
        SensorEventSink? sink = null;
        await using var session = new SensorSession(() =>
        {
            receiver.Owner = Environment.CurrentManagedThreadId;
            sink = new SensorEventSink(receiver);
            packet = new Packet(sink);
            return new Backend(sink, packet);
        }, apartment);
        await session.QueryAsync(field);
        Assert.NotNull(packet);
        Report report = await Task.Run(() => InvokeFromMta(packet, field))
            .WaitAsync(TimeSpan.FromSeconds(20));
        // 呼出しとCOM参照の解放後にも、コピーした値が有効なことを確認します。
        Assert.Equal(Expected(field.Kind), receiver.Value);
        Assert.Equal(1, receiver.Calls);
        Assert.Equal(1, report.Reads);
        Assert.Equal(field.Key, report.Key);
        Assert.Equal(ApartmentState.MTA, report.Apartment);
        Assert.NotEqual(receiver.Owner, report.Thread);
        Assert.Null(await session.TakeCallbackFailureAsync());
        await session.DisposeAsync();
        Assert.Equal(receiver.Owner, apartment.InitializedThread);
        Assert.Equal(receiver.Owner, apartment.UninitializedThread);
        Assert.Equal(1, apartment.Uninitializes);
        Assert.True(apartment.InitializeResult >= 0);
        Assert.True(apartment.Dpi > 0);
        Assert.InRange(apartment.Awareness, 0, 2);
        output.WriteLine($"selector={selector}; copied={receiver.Value}; owner={receiver.Owner}; report-thread={report.Thread}; report-apartment={report.Apartment}; reads={report.Reads}; init=0x{apartment.InitializeResult:x8}; uninitialize=1; owner-dpi-api={apartment.Dpi}; owner-awareness={apartment.Awareness}");
    }

    private static object Expected(SensorDataValueKind kind) => kind switch
    {
        SensorDataValueKind.Boolean => true,
        SensorDataValueKind.Numeric => 42.5d,
        SensorDataValueKind.Integer => 1234567890123L,
        SensorDataValueKind.String => "センサー report 文字列",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static unsafe Report InvokeFromMta(Packet packet, SensorDataField field)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0), new nint(-1));
        nint proxy = 0;
        nint reportPointer = 0;
        var report = new Report(field.Kind);
        try
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            Guid contract = packet.Contract;
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(
                packet.Take(), in contract, out proxy), new nint(-1));
            Assert.NotEqual(0, proxy);
            reportPointer = Marshal.GetComInterfaceForObject(report, typeof(IReport));
            Assert.NotEqual(0, reportPointer);
            nint table = Marshal.ReadIntPtr(proxy);
            nint data = Marshal.ReadIntPtr(table, 4 * IntPtr.Size);
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)data)
                (proxy, 0, reportPointer));
            return report;
        }
        finally
        {
            if (reportPointer != 0)
            {
                Marshal.Release(reportPointer);
            }

            if (proxy != 0)
            {
                Marshal.Release(proxy);
            }

            GC.KeepAlive(report);
            CoUninitialize();
        }
    }

    // SDK 10.0.26100.0 sensorsapi.hのISensorDataReportと同じIID・slot順です。
    [ComImport]
    [ComVisible(true)]
    [Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IReport
    {
        [PreserveSig]
        int GetTimestamp(nint timestamp);

        [PreserveSig]
        int GetSensorValue(nint key, nint value);

        [PreserveSig]
        int GetSensorValues(nint keys, out nint values);
    }

    // 標準marshalerを使い、agile CCWによる所有STAでの直接実行を避けます。
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class Report(SensorDataValueKind kind)
        : StandardOleMarshalObject, IReport
    {
        internal int Reads;
        internal int Thread;
        internal ApartmentState Apartment;
        internal SensorPropertyKey? Key;

        public int GetTimestamp(nint timestamp) => unchecked((int)0x80004001);

        public int GetSensorValue(nint key, nint value)
        {
            Reads++;
            Thread = Environment.CurrentManagedThreadId;
            Apartment = System.Threading.Thread.CurrentThread.GetApartmentState();
            Key = Marshal.PtrToStructure<SensorPropertyKey>(key);
            for (int offset = 0; offset < (IntPtr.Size == 8 ? 24 : 16); offset += 8)
            {
                Marshal.WriteInt64(value, offset, 0);
            }

            // out PROPVARIANTの所有権はCOMへ渡し、stub側の後処理に任せます。
            switch (kind)
            {
                case SensorDataValueKind.Boolean:
                    Marshal.WriteInt16(value, 11);
                    Marshal.WriteInt16(value, 8, -1);
                    break;
                case SensorDataValueKind.Numeric:
                    Marshal.WriteInt16(value, 5);
                    Marshal.WriteInt64(value, 8, BitConverter.DoubleToInt64Bits(42.5d));
                    break;
                case SensorDataValueKind.Integer:
                    Marshal.WriteInt16(value, 20);
                    Marshal.WriteInt64(value, 8, 1234567890123L);
                    break;
                case SensorDataValueKind.String:
                    Marshal.WriteInt16(value, 31);
                    Marshal.WriteIntPtr(value, 8,
                        Marshal.StringToCoTaskMemUni((string)Expected(kind)));
                    break;
            }

            return 0;
        }

        public int GetSensorValues(nint keys, out nint values)
        {
            values = 0;
            return unchecked((int)0x80004001);
        }
    }

    private sealed class Receiver(SensorDataField field) : ISensorEventReceiver
    {
        internal int Owner;
        internal int Calls;
        internal object? Value;

        public void DataUpdated(nint sensor, nint report)
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.NotEqual(0, report);
            Calls++;
            Value = SensorReportReader.Read(sensor, report, field);
        }

        public void StateChanged(nint sensor, int state)
        {
        }

        public void Event(nint sensor, in Guid eventId, nint data)
        {
        }

        public void Leave(in Guid sensorId)
        {
        }
    }

    private sealed class Backend(SensorEventSink sink, Packet packet) : ISensorSessionController
    {
        public int Refresh() => 0;

        public bool TryRead(SensorDataField field, out object? value)
        {
            value = null;
            return false;
        }

        public Exception? TakeCallbackFailure() => sink.TakeFailure();

        public void Dispose() => packet.Dispose();
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
