using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の取得先と所有するPROPVARIANTだけを使い、機器・権限・GUIには触れません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorReportReaderTests
{
    [Fact]
    public void CachedReportIsRetainedWithoutFetchingSensorData()
    {
        var api = new Api { ReportReferences = 1 };
        Assert.Equal(12.5d, Read(api, cached: true));
        Assert.Equal(["add-report", "value", "release-report"], api.Events);
        Assert.Equal(1, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
    }

    [Fact]
    public void FreshReportIsOwnedWithoutAddingAnotherReportReference()
    {
        var api = new Api();
        Assert.Equal(12.5d, Read(api));
        Assert.Equal(["add-sensor", "data", "value", "release-report", "release-sensor"], api.Events);
        Assert.Equal(0, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
        Assert.Equal(Field().Key, api.LastKey);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x800700e8))]
    [InlineData(unchecked((int)0x80004005))]
    public void DataFailurePreservesHResultAndReleasesAnyReturnedReport(int result)
    {
        var api = new Api { DataResult = result };
        Exception error = Assert.ThrowsAny<Exception>(() => Read(api));
        Assert.Equal(result, error.HResult);
        Assert.DoesNotContain("value", api.Events);
        Assert.Equal(0, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
    }

    [Fact]
    public void SuccessfulDataCallMustReturnAReport()
    {
        var api = new Api { ReturnNull = true };
        COMException error = Assert.Throws<COMException>(() => Read(api));
        Assert.Equal(unchecked((int)0x80004003), error.HResult);
        Assert.Equal(["add-sensor", "data", "release-sensor"], api.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FailedFieldBecomesAnEmptyPropertyBeforeTypeConversion(int kind)
    {
        var api = new Api { ValueResult = unchecked((int)0x80070490) };
        api.WriteValue = property =>
        {
            Marshal.WriteInt16(property, (short)VarEnum.VT_LPWSTR);
            Marshal.WriteIntPtr(property, 8, Marshal.StringToCoTaskMemUni("部分値"));
        };
        var propertyApi = new PropertyApi();
        object value = SensorReportReader.Read(api.Sensor, 0,
            Field((SensorDataValueKind)kind), api, propertyApi);
        switch ((SensorDataValueKind)kind)
        {
            case SensorDataValueKind.Numeric:
                Assert.Equal(unchecked((long)0xfff8000000000000),
                    BitConverter.DoubleToInt64Bits(Assert.IsType<double>(value)));
                break;
            case SensorDataValueKind.Boolean:
                Assert.False(Assert.IsType<bool>(value));
                break;
            case SensorDataValueKind.Integer:
                Assert.Equal(0L, Assert.IsType<long>(value));
                break;
            case SensorDataValueKind.String:
                Assert.Equal("<no value>", Assert.IsType<string>(value));
                break;
        }

        Assert.Equal([VarEnum.VT_LPWSTR, VarEnum.VT_EMPTY], propertyApi.Cleared);
        Assert.Equal(0, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
    }

    [Fact]
    public void NonnegativeResultsDoNotEraseTheValue()
    {
        var api = new Api { DataResult = 1, ValueResult = 1 };
        Assert.Equal(12.5d, Read(api));
    }

    [Fact]
    public void CachedOwnerCanReleaseItsReferenceDuringValueRetrieval()
    {
        var api = new Api { ReportReferences = 1 };
        api.OnValue = () => api.Release(api.Report);
        Assert.Equal(12.5d, Read(api, cached: true));
        Assert.Equal(0, api.ReportReferences);
    }

    [Fact]
    public void SensorOwnerCanReleaseItsReferenceDuringDataRetrieval()
    {
        var api = new Api();
        api.OnData = () => api.Release(api.Sensor);
        Assert.Equal(12.5d, Read(api));
        Assert.Equal(0, api.SensorReferences);
        Assert.Equal(0, api.ReportReferences);
    }

    [Fact]
    public void ValueExceptionStillClearsStorageBeforeReleasingReport()
    {
        var api = new Api();
        var failure = new InvalidOperationException("取得中断");
        api.WriteValue = property =>
        {
            Marshal.WriteInt16(property, (short)VarEnum.VT_LPWSTR);
            Marshal.WriteIntPtr(property, 8, Marshal.StringToCoTaskMemUni("部分値"));
            throw failure;
        };
        var propertyApi = new PropertyApi
        {
            OnClear = () => Assert.Equal(1, api.ReportReferences)
        };
        Exception? actual = Record.Exception(() => SensorReportReader.Read(
            api.Sensor, 0, Field(), api, propertyApi));
        Assert.Same(failure, actual);
        Assert.Equal([VarEnum.VT_LPWSTR], propertyApi.Cleared);
        Assert.Equal(0, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
    }

    [Fact]
    public void DataExceptionReleasesItsOutputAndSensor()
    {
        var failure = new InvalidOperationException("report取得中断");
        var api = new Api { OnData = () => throw failure };
        Assert.Same(failure, Record.Exception(() => Read(api)));
        Assert.Equal(0, api.ReportReferences);
        Assert.Equal(1, api.SensorReferences);
    }

    [Fact]
    public void EmptyBarcodeSkipsAllReportOperations()
    {
        var api = new Api();
        Assert.Equal(string.Empty, SensorReportReader.Read(0, 0,
            new SensorDataField(Guid.Empty, null, SensorDataValueKind.EmptyString), api));
        Assert.Empty(api.Events);
    }

    [Fact]
    public void MissingKeyInvalidKindAndMissingSourceFailBeforeComCalls()
    {
        var api = new Api();
        Assert.Throws<ArgumentException>(() => SensorReportReader.Read(0, 0, Field(), api));
        Assert.Throws<ArgumentException>(() => SensorReportReader.Read(api.Sensor, 0,
            new SensorDataField(Guid.Empty, null, SensorDataValueKind.Numeric), api));
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorReportReader.Read(api.Sensor, 0,
            new SensorDataField(Guid.Empty, null, (SensorDataValueKind)(-1)), api));
        Assert.Empty(api.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadAndReleaseFailuresAreAllPreserved(bool cached)
    {
        var readError = new InvalidOperationException("読み取り失敗");
        var reportError = new InvalidOperationException("レポート解放失敗");
        var sensorError = new InvalidOperationException("センサー解放失敗");
        var api = new Api
        {
            ReportReferences = cached ? 1 : 0,
            OnValue = () => throw readError
        };
        api.OnRelease = instance => throw (instance == api.Report
            ? reportError : sensorError);

        AggregateException failure = Assert.Throws<AggregateException>(
            () => Read(api, cached));
        IReadOnlyCollection<Exception> errors =
            failure.Flatten().InnerExceptions;
        Assert.Equal(cached ? 2 : 3, errors.Count);
        Assert.Contains(readError, errors);
        Assert.Contains(reportError, errors);
        if (!cached)
        {
            Assert.Contains(sensorError, errors);
        }

        Assert.Equal(1, api.SensorReferences);
        Assert.Equal(cached ? 1 : 0, api.ReportReferences);
    }

    private static object Read(Api api, bool cached = false) =>
        SensorReportReader.Read(cached ? 0 : api.Sensor,
            cached ? api.Report : 0, Field(), api);

    private static SensorDataField Field(SensorDataValueKind kind = SensorDataValueKind.Numeric)
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field with
        {
            Kind = kind
        };
    }

    private sealed class PropertyApi : ISensorPropertyValueNativeApi
    {
        internal List<VarEnum> Cleared { get; } = [];

        internal Action? OnClear
        {
            get; init;
        }

        public bool ReadBoolean(nint value) => SensorPropertyValueNativeApi.Instance.ReadBoolean(value);

        public string ReadString(nint value) => SensorPropertyValueNativeApi.Instance.ReadString(value);

        public void Clear(nint value)
        {
            OnClear?.Invoke();
            Cleared.Add((VarEnum)Marshal.ReadInt16(value));
            SensorPropertyValueNativeApi.Instance.Clear(value);
        }
    }

    private sealed class Api : ISensorReportNativeApi
    {
        internal readonly nint Sensor = 101;
        internal readonly nint Report = 202;
        internal int SensorReferences = 1;
        internal int ReportReferences;
        internal int DataResult;
        internal int ValueResult;
        internal bool ReturnNull;
        internal Action? OnValue;
        internal Action? OnData;
        internal Action<nint>? OnRelease;
        internal Action<nint>? WriteValue;
        internal SensorPropertyKey? LastKey;
        internal List<string> Events { get; } = [];

        public void AddRef(nint instance)
        {
            if (instance == Sensor)
            {
                SensorReferences++;
                Events.Add("add-sensor");
            }
            else
            {
                Assert.Equal(Report, instance);
                ReportReferences++;
                Events.Add("add-report");
            }
        }

        public void Release(nint instance)
        {
            if (instance == Sensor)
            {
                Assert.True(SensorReferences-- > 0);
                Events.Add("release-sensor");
            }
            else
            {
                Assert.Equal(Report, instance);
                Assert.True(ReportReferences-- > 0);
                Events.Add("release-report");
            }

            OnRelease?.Invoke(instance);
        }

        public int GetData(nint sensor, out nint report)
        {
            Assert.Equal(Sensor, sensor);
            Events.Add("data");
            report = ReturnNull ? 0 : Report;
            if (report != 0)
            {
                ReportReferences++;
            }

            OnData?.Invoke();
            return DataResult;
        }

        public int GetSensorValue(nint report, in SensorPropertyKey key, nint propertyValue)
        {
            Assert.Equal(Report, report);
            Assert.True(ReportReferences > 0);
            Events.Add("value");
            LastKey = key;
            OnValue?.Invoke();
            if (WriteValue is not null)
            {
                WriteValue(propertyValue);
            }
            else
            {
                Marshal.WriteInt16(propertyValue, (short)VarEnum.VT_R8);
                Marshal.WriteInt64(propertyValue, 8, BitConverter.DoubleToInt64Bits(12.5));
            }

            return ValueResult;
        }
    }
}
