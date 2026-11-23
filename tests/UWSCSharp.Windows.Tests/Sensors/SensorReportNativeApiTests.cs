using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するCOM形式のメモリとunmanaged入口でABIを確認し、COM生成・機器・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorReportNativeApiTests
{
    private static readonly ConcurrentDictionary<nint, Fixture> Instances = new();

    [Fact]
    public void NativeSlotsPassThisPropertyKeyAndOutputStorage()
    {
        using var fixture = new Fixture();
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        object value = SensorReportReader.Read(fixture.Sensor, 0, field);
        Assert.Equal(12.5d, Assert.IsType<double>(value));
        Assert.Equal(field.Key, fixture.LastKey);
        Assert.Equal(1, fixture.SensorReferences);
        Assert.Equal(0, fixture.ReportReferences);
        Assert.Equal(["add-sensor", "data", "value", "release-report", "release-sensor"],
            fixture.Events);
        Assert.All(fixture.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Fact]
    public void CachedNativeReportKeepsItsOwnersReference()
    {
        using var fixture = new Fixture { ReportReferences = 1 };
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        Assert.Equal(12.5d, SensorReportReader.Read(0, fixture.Report, field));
        Assert.Equal(1, fixture.ReportReferences);
        Assert.Equal(["add-report", "value", "release-report"], fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeNegativeHResultsKeepDataFailureAndFieldFailureDistinct(bool dataFailure)
    {
        const int failure = unchecked((int)0x80070005);
        using var fixture = new Fixture
        {
            DataResult = dataFailure ? failure : 0,
            ValueResult = dataFailure ? 0 : failure
        };
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        if (dataFailure)
        {
            Exception error = Assert.ThrowsAny<Exception>(() =>
                SensorReportReader.Read(fixture.Sensor, 0, field));
            Assert.Equal(failure, error.HResult);
            Assert.DoesNotContain("value", fixture.Events);
        }
        else
        {
            object value = SensorReportReader.Read(fixture.Sensor, 0, field);
            Assert.Equal(unchecked((long)0xfff8000000000000),
                BitConverter.DoubleToInt64Bits(Assert.IsType<double>(value)));
        }

        Assert.Equal(1, fixture.SensorReferences);
        Assert.Equal(0, fixture.ReportReferences);
    }

    [Fact]
    public void NullInterfaceDoesNotReadAVtable()
    {
        Assert.Throws<ArgumentException>(() => SensorReportNativeApi.Instance.GetData(0, out _));
        var key = new SensorPropertyKey(Guid.Empty, 0);
        Assert.Throws<ArgumentException>(() => SensorReportNativeApi.Instance.GetSensorValue(0, in key, 0));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint instance)
    {
        Fixture fixture = Instances[instance];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        if (instance == fixture.Sensor)
        {
            fixture.Events.Add("add-sensor");
            return (uint)++fixture.SensorReferences;
        }

        fixture.Events.Add("add-report");
        return (uint)++fixture.ReportReferences;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint instance)
    {
        Fixture fixture = Instances[instance];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        if (instance == fixture.Sensor)
        {
            fixture.Events.Add("release-sensor");
            return (uint)--fixture.SensorReferences;
        }

        fixture.Events.Add("release-report");
        return (uint)--fixture.ReportReferences;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(nint sensor, nint report)
    {
        Fixture fixture = Instances[sensor];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        fixture.Events.Add("data");
        fixture.ReportReferences++;
        Marshal.WriteIntPtr(report, fixture.Report);
        return fixture.DataResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetValue(nint report, nint key, nint propertyValue)
    {
        Fixture fixture = Instances[report];
        fixture.Threads.Add(Environment.CurrentManagedThreadId);
        fixture.Events.Add("value");
        fixture.LastKey = Marshal.PtrToStructure<SensorPropertyKey>(key);
        Marshal.WriteInt16(propertyValue, (short)VarEnum.VT_R8);
        Marshal.WriteInt64(propertyValue, 8, BitConverter.DoubleToInt64Bits(12.5));
        return fixture.ValueResult;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly nint Sensor;
        internal readonly nint Report;
        internal int SensorReferences = 1;
        internal int ReportReferences;
        internal int DataResult;
        internal int ValueResult;
        internal SensorPropertyKey? LastKey;
        internal List<string> Events { get; } = [];
        internal List<int> Threads { get; } = [];
        private readonly List<nint> allocations = [];

        internal unsafe Fixture()
        {
            nint sensorTable = Allocate(14 * IntPtr.Size);
            nint reportTable = Allocate(5 * IntPtr.Size);
            Sensor = Allocate(IntPtr.Size);
            Report = Allocate(IntPtr.Size);
            Marshal.WriteIntPtr(Sensor, sensorTable);
            Marshal.WriteIntPtr(Report, reportTable);
            foreach (nint table in new[] { sensorTable, reportTable })
            {
                Marshal.WriteIntPtr(table, IntPtr.Size,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef);
                Marshal.WriteIntPtr(table, 2 * IntPtr.Size,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release);
            }

            Marshal.WriteIntPtr(sensorTable, 13 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetData);
            Marshal.WriteIntPtr(reportTable, 4 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&GetValue);
            Instances.TryAdd(Sensor, this);
            Instances.TryAdd(Report, this);
        }

        private nint Allocate(int size)
        {
            nint allocation = Marshal.AllocHGlobal(size);
            allocations.Add(allocation);
            Marshal.Copy(new byte[size], 0, allocation, size);
            return allocation;
        }

        public void Dispose()
        {
            Instances.TryRemove(Sensor, out _);
            Instances.TryRemove(Report, out _);
            foreach (nint allocation in allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }
    }
}
