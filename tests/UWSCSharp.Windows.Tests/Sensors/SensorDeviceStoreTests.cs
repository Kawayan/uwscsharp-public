using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有参照を偽の取得先で数え、機器・権限・COM生成・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorDeviceStoreTests
{
    private static readonly Guid FirstId = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = new("10000000-0000-0000-0000-000000000002");

    [Fact]
    public void CategorySelectionKeepsRegistrationOrderAndAdvancesAfterRemoval()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration first = store.Register(FirstId, Field().CategoryId, 101);
        store.Register(SecondId, Field().CategoryId, 102);
        AssertValue(store, 1.5);
        Assert.True(store.Remove(first));
        AssertValue(store, 2.5);
        Assert.Equal(1, api.References[101]);
    }

    [Fact]
    public void SameIdKeepsTheExistingDeviceAndReport()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration first = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(first, 1001);
        Assert.Same(first, store.Register(FirstId, Guid.Empty, 102));
        Assert.Equal(1, api.References[102]);
        AssertValue(store, 10.5);
    }

    [Fact]
    public void MissingCategoryDoesNotConstructAFallbackValue()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        store.Register(FirstId, Guid.Empty, 101);
        api.Calls.Clear();
        Assert.False(store.TryRead(Field(), out object? value));
        Assert.Null(value);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void BarcodeStillRequiresADeviceButDoesNotFetchAReport()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        Assert.True(SensorDataFieldCatalog.TryGet(41, out SensorDataField field));
        Assert.False(store.TryRead(field, out _));
        store.Register(FirstId, field.CategoryId, 101);
        api.Calls.Clear();
        Assert.True(store.TryRead(field, out object? value));
        Assert.Equal(string.Empty, value);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void ReportNotificationReplacesTheValueAndNullReturnsToGetData()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        Assert.True(store.UpdateReport(device, 1001));
        AssertValue(store, 10.5);
        Assert.True(store.UpdateReport(device, 1002));
        Assert.Equal(1, api.References[1001]);
        AssertValue(store, 20.5);
        Assert.True(store.UpdateReport(device, 0));
        Assert.Equal(1, api.References[1002]);
        AssertValue(store, 1.5);
    }

    [Fact]
    public void SameReportIsRetainedBeforeThePreviousReferenceIsReleased()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        api.Release(1001);
        Assert.Equal(1, api.References[1001]);
        Assert.True(store.UpdateReport(device, 1001));
        Assert.Equal(1, api.References[1001]);
        AssertValue(store, 10.5);
    }

    [Fact]
    public void OldAndForeignRegistrationsCannotAffectAReplacement()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        using var other = new SensorDeviceStore(api);
        SensorDeviceRegistration old = store.Register(FirstId, Field().CategoryId, 101);
        SensorDeviceRegistration foreign = other.Register(FirstId, Field().CategoryId, 101);
        store.Remove(old);
        SensorDeviceRegistration current = store.Register(FirstId, Field().CategoryId, 102);
        api.Calls.Clear();
        Assert.False(store.UpdateReport(old, 1001));
        Assert.False(store.UpdateReport(foreign, 1001));
        Assert.False(store.Remove(old));
        Assert.False(store.Remove(foreign));
        Assert.Empty(api.Calls);
        AssertValue(store, 2.5);
        Assert.True(store.Remove(current));
    }

    [Fact]
    public void RemovingTheDeviceDuringReadKeepsBothSnapshotReferencesAlive()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        api.OnValue = () =>
        {
            Assert.True(store.Remove(device));
            Assert.Equal(2, api.References[101]);
            Assert.True(api.References[1001] > 1);
        };
        AssertValue(store, 10.5);
        Assert.Equal(1, api.References[101]);
        Assert.Equal(1, api.References[1001]);
        Assert.False(store.TryRead(Field(), out _));
    }

    [Fact]
    public void ReplacingTheReportDuringReadDoesNotChangeTheCapturedReport()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        api.OnValue = () => store.UpdateReport(device, 1002);
        AssertValue(store, 10.5);
        Assert.Equal(1, api.References[1001]);
        api.OnValue = null;
        AssertValue(store, 20.5);
    }

    [Fact]
    public void DisposalDuringReadDefersReleaseUntilTheCopyCompletes()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        api.OnValue = store.Dispose;
        AssertValue(store, 10.5);
        Assert.Equal(1, api.References[101]);
        Assert.Equal(1, api.References[1001]);
        Assert.Throws<ObjectDisposedException>(() => store.TryRead(Field(), out _));
        Assert.False(store.UpdateReport(device, 1002));
        store.Dispose();
    }

    [Fact]
    public void ReadFailureStillReturnsAllRemovedReferences()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        var failure = new InvalidOperationException("読取り中断");
        api.OnValue = () =>
        {
            store.Remove(device);
            throw failure;
        };
        Assert.Same(failure, Record.Exception(() => store.TryRead(Field(), out _)));
        Assert.Equal(1, api.References[101]);
        Assert.Equal(1, api.References[1001]);
    }

    [Fact]
    public void RegistrationDoesNotCommitAfterReentrantDisposal()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        api.OnAdd = store.Dispose;
        Assert.Throws<ObjectDisposedException>(() => store.Register(FirstId, Field().CategoryId, 101));
        Assert.Equal(1, api.References[101]);
        store.Dispose();
    }

    [Fact]
    public void ReportRetentionDoesNotCommitAfterReentrantDisposal()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        api.OnAdd = store.Dispose;
        Assert.False(store.UpdateReport(device, 1001));
        Assert.Equal(1, api.References[1001]);
        Assert.Equal(1, api.References[101]);
        store.Dispose();
    }

    [Fact]
    public void ReentrantRegistrationWinsWithoutLeakingTheProvisionalReference()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration? inner = null;
        api.OnAdd = () => inner = store.Register(FirstId, Field().CategoryId, 102);
        SensorDeviceRegistration outer = store.Register(FirstId, Field().CategoryId, 101);
        Assert.Same(inner, outer);
        Assert.Equal(1, api.References[101]);
        AssertValue(store, 2.5);
    }

    [Fact]
    public void ReentrantReportUpdateIsNotOverwrittenAfterOldRelease()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.UpdateReport(device, 1001);
        api.OnRelease = () => store.UpdateReport(device, 1003);
        store.UpdateReport(device, 1002);
        Assert.Equal(1, api.References[1001]);
        Assert.Equal(1, api.References[1002]);
        AssertValue(store, 30.5);
    }

    [Fact]
    public void DisposalAttemptsOtherReferencesAfterOneReleaseFails()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        SensorDeviceRegistration first = store.Register(FirstId, Field().CategoryId, 101);
        store.Register(SecondId, Field().CategoryId, 102);
        store.UpdateReport(first, 1001);
        api.OnRelease = () => throw new InvalidOperationException("解放中断");
        Assert.Single(Assert.Throws<AggregateException>(store.Dispose).InnerExceptions);
        Assert.Equal(1, api.References[1001]);
        Assert.Equal(1, api.References[101]);
        Assert.Equal(1, api.References[102]);
        api.Calls.Clear();
        store.Dispose();
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void DisposalPreservesBothFailuresAndReleasesTheNextDevice()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        SensorDeviceRegistration first = store.Register(FirstId, Field().CategoryId, 101);
        store.Register(SecondId, Field().CategoryId, 102);
        store.UpdateReport(first, 1001);
        var reportError = new InvalidOperationException("レポートの解放失敗");
        var sensorError = new InvalidOperationException("センサーの解放失敗");
        api.OnRelease = () =>
        {
            api.OnRelease = () => throw sensorError;
            throw reportError;
        };

        AggregateException failure = Assert.Throws<AggregateException>(store.Dispose);
        Assert.Equal<Exception>([reportError, sensorError], failure.Flatten().InnerExceptions);
        Assert.Equal(1, api.References[102]);
    }

    [Fact]
    public void WrongThreadOperationsDoNotTouchComReferences()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        api.Calls.Clear();
        var errors = new List<Exception?>();
        var thread = new Thread(() =>
        {
            errors.Add(Record.Exception(() => store.Register(SecondId, Field().CategoryId, 102)));
            errors.Add(Record.Exception(() => store.UpdateReport(device, 1001)));
            errors.Add(Record.Exception(() => store.Remove(device)));
            errors.Add(Record.Exception(() => store.TryRead(Field(), out _)));
            errors.Add(Record.Exception(store.Dispose));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.All(errors, error => Assert.IsType<InvalidOperationException>(error));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void ClosedStoreRejectsRegistrationAndIgnoresLateNotifications()
    {
        var api = new Api();
        var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(FirstId, Field().CategoryId, 101);
        store.Dispose();
        api.Calls.Clear();
        Assert.Throws<ObjectDisposedException>(() => store.Register(FirstId, Field().CategoryId, 101));
        Assert.False(store.UpdateReport(device, 1001));
        Assert.False(store.Remove(device));
        Assert.Empty(api.Calls);
    }

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private static void AssertValue(SensorDeviceStore store, double expected)
    {
        Assert.True(store.TryRead(Field(), out object? value));
        Assert.Equal(expected, Assert.IsType<double>(value));
    }

    private sealed class Api : ISensorReportNativeApi
    {
        internal Dictionary<nint, int> References
        {
            get;
        } = new()
        {
            [101] = 1,
            [102] = 1,
            [1001] = 1,
            [1002] = 1,
            [1003] = 1,
            [2101] = 0,
            [2102] = 0
        };
        internal List<string> Calls { get; } = [];
        internal Action? OnValue;
        internal Action? OnAdd;
        internal Action? OnRelease;

        public void AddRef(nint pointer)
        {
            Assert.True(References[pointer] > 0);
            References[pointer]++;
            Calls.Add($"add:{pointer}");
            Action? callback = OnAdd;
            OnAdd = null;
            callback?.Invoke();
        }

        public void Release(nint pointer)
        {
            Assert.True(References[pointer] > 0);
            References[pointer]--;
            Calls.Add($"release:{pointer}");
            Action? callback = OnRelease;
            OnRelease = null;
            callback?.Invoke();
        }

        public int GetData(nint sensor, out nint report)
        {
            Assert.True(References[sensor] > 1);
            report = sensor + 2000;
            References[report]++;
            Calls.Add($"data:{sensor}");
            return 0;
        }

        public int GetSensorValue(nint report, in SensorPropertyKey key, nint propertyValue)
        {
            Assert.True(References[report] > 0);
            OnValue?.Invoke();
            Assert.True(References[report] > 0);
            double value = report switch
            {
                1001 => 10.5,
                1002 => 20.5,
                1003 => 30.5,
                2101 => 1.5,
                2102 => 2.5,
                _ => throw new InvalidOperationException()
            };
            Marshal.WriteInt16(propertyValue, (short)VarEnum.VT_R8);
            Marshal.WriteInt64(propertyValue, 8, BitConverter.DoubleToInt64Bits(value));
            Calls.Add($"value:{report}");
            return 0;
        }
    }
}
