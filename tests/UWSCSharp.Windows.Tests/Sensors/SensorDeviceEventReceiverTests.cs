using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の機器とreportで通知中の値を確認し、実COM・機器・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorDeviceEventReceiverTests
{
    [Fact]
    public void ReportIsVisibleDuringNotificationAndReleasedBeforeTheNextRead()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        var id = Guid.NewGuid();
        SensorDeviceRegistration device = store.Register(id, Field().CategoryId, 101);
        int updates = 0;
        var sink = new SensorEventSink(new SensorDeviceEventReceiver(store, device, () =>
        {
            updates++;
            Assert.True(store.TryRead(Field(), out object? value));
            Assert.Equal(10.5d, value);
            Assert.True(api.ReportReferences > 1);
        }));
        Assert.Equal(0, sink.OnDataUpdated(101, 202));
        Assert.Equal(1, updates);
        Assert.Equal(1, api.ReportReferences);
        Assert.True(store.TryRead(Field(), out object? after));
        Assert.Equal(1.5d, after);
        Assert.Equal(0, api.FreshReferences);
        var other = Guid.NewGuid();
        Assert.Equal(0, sink.OnLeave(in other));
        Assert.True(store.Contains(id));
        Assert.Equal(0, sink.OnLeave(in id));
        Assert.False(store.Contains(id));
        Assert.Equal(1, api.SensorReferences);
        store.Register(id, Field().CategoryId, 101);
        Assert.Equal(0, sink.OnDataUpdated(101, 202));
        Assert.Equal(0, sink.OnStateChanged(101, 3));
        Assert.Equal(0, sink.OnLeave(in id));
        Assert.True(store.Contains(id));
        Assert.Equal(1, updates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReceiverReleasesNotificationReportAfterCallbackFailureOrDisposal(bool dispose)
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(Guid.NewGuid(), Field().CategoryId, 101);
        var failure = new InvalidOperationException("通知処理の失敗");
        var sink = new SensorEventSink(new SensorDeviceEventReceiver(store, device, () =>
        {
            if (dispose)
            {
                store.Dispose();
            }
            else
            {
                throw failure;
            }
        }));
        int result = sink.OnDataUpdated(101, 202);
        if (dispose)
        {
            Assert.Equal(0, result);
            Assert.Equal(1, api.SensorReferences);
        }
        else
        {
            Assert.Equal(failure.HResult, result);
            Assert.Same(failure, sink.TakeFailure());
        }

        Assert.Equal(1, api.ReportReferences);
    }

    [Fact]
    public void StateNotificationAndCustomEventDoNotFetchOrRetainReports()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(Guid.NewGuid(), Field().CategoryId, 101);
        int state = 0;
        var sink = new SensorEventSink(new SensorDeviceEventReceiver(store, device,
            stateChanged: value => state = value));
        Assert.Equal(0, sink.OnStateChanged(101, 4));
        Assert.Equal(4, state);
        var eventId = Guid.NewGuid();
        Assert.Equal(0, sink.OnEvent(101, in eventId, 303));
        Assert.Equal(1, api.ReportReferences);
        Assert.Equal(0, api.FreshReferences);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportReleaseFailurePreservesTheNotificationFailure(bool callbackFails)
    {
        var cleanup = new InvalidOperationException("report解放の失敗");
        var primary = new ArgumentException("通知処理の失敗");
        var api = new Api { ReportReleaseFailure = cleanup };
        using var store = new SensorDeviceStore(api);
        SensorDeviceRegistration device = store.Register(Guid.NewGuid(), Field().CategoryId, 101);
        var observed = new SensorCallbackFailure();
        var sink = new SensorEventSink(new SensorDeviceEventReceiver(store, device, () =>
        {
            if (callbackFails)
            {
                throw primary;
            }
        }), observed);

        int result = sink.OnDataUpdated(101, 202);
        Exception expected = callbackFails ? primary : cleanup;
        Assert.Equal(expected.HResult, result);
        Assert.Same(expected, sink.TakeFailure());
        Assert.Same(expected, observed.Take());
        Assert.Null(observed.Take());
        if (callbackFails)
        {
            Assert.Same(cleanup, primary.Data["UWSCSharp.SensorNotificationCleanup"]);
        }

        Assert.Equal(1, api.ReportReferences);
        Assert.True(store.TryRead(Field(), out object? after));
        Assert.Equal(1.5d, after);
        Assert.Equal(0, api.FreshReferences);
    }

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private sealed class Api : ISensorReportNativeApi
    {
        internal int SensorReferences = 1;
        internal int ReportReferences = 1;
        internal int FreshReferences;
        internal Exception? ReportReleaseFailure;

        public void AddRef(nint instance)
        {
            if (instance == 101)
            {
                SensorReferences++;
            }
            else
            {
                Assert.Equal(202, instance);
                ReportReferences++;
            }
        }

        public void Release(nint instance)
        {
            if (instance == 101)
            {
                SensorReferences--;
            }
            else if (instance == 202)
            {
                ReportReferences--;
                if (ReportReleaseFailure is not null)
                {
                    throw ReportReleaseFailure;
                }
            }
            else
            {
                Assert.Equal(303, instance);
                FreshReferences--;
            }
        }

        public int GetData(nint sensor, out nint report)
        {
            Assert.Equal(101, sensor);
            report = 303;
            FreshReferences++;
            return 0;
        }

        public int GetSensorValue(nint report, in SensorPropertyKey key, nint value)
        {
            Marshal.WriteInt16(value, (short)VarEnum.VT_R8);
            Marshal.WriteInt64(value, 8, BitConverter.DoubleToInt64Bits(report == 202 ? 10.5 : 1.5));
            return 0;
        }
    }
}
