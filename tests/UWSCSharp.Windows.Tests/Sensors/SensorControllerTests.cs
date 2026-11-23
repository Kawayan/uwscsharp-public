using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 生成・列挙・通知を偽APIで統合し、実COM生成・機器・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorControllerTests
{
    [Fact]
    public void RefreshAndNotificationConnectCreationEnumerationReadingAndCleanup()
    {
        var api = new Api();
        SensorController? current = null;
        using SensorController controller = Create(api, () => AssertValue(current!, 10.5));
        current = controller;
        Assert.Equal(0, controller.Refresh());
        Assert.Equal(2, api.References[1]);
        Assert.Equal(3, api.References[201]);
        Assert.True(api.Calls.IndexOf("set:1") < api.Calls.IndexOf("permission"));
        AssertValue(controller, 1.5);
        Assert.Equal(0, api.Device(201).OnDataUpdated(201, 301));
        Assert.Equal(1, api.References[301]);
        AssertValue(controller, 1.5);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void LeaveDetachesImmediatelyAndAnOldSinkCannotRemoveTheReplacement()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        SensorEventSink old = api.Device(201);
        Guid id = Api.Id(201);
        Assert.Equal(0, old.OnLeave(in id));
        Assert.Equal(1, api.References[201]);
        AssertValue(controller, 2.5);
        Assert.Equal(0, api.Manager().OnSensorEnter(201, 0));
        Assert.NotSame(old, api.Device(201));
        Assert.Equal(0, old.OnLeave(in id));
        Assert.Equal(3, api.References[201]);
        AssertValue(controller, 2.5);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void RecreatingTheManagerKeepsExistingDeviceConnectionsAndOrder()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        SensorManagerEventSink oldManager = api.Manager();
        SensorEventSink first = api.Device(201);
        api.Items = [202, 201];
        Assert.Equal(0, controller.Refresh());
        Assert.Equal(0, api.References[1]);
        Assert.Equal(2, api.References[2]);
        Assert.Same(first, api.Device(201));
        Assert.Equal(1, api.Calls.Count(call => call == "set:201"));
        Assert.Equal(0, oldManager.OnSensorEnter(201, 0));
        AssertValue(controller, 1.5);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedCreationReturnsTheHResultAndReleasesPartialOutput(bool output)
    {
        var api = new Api { CreateResult = unchecked((int)0x80040154), NullManager = !output };
        using SensorController controller = Create(api);
        Assert.Equal(api.CreateResult, controller.Refresh());
        Assert.False(controller.TryRead(Field(), out _));
        Assert.DoesNotContain("permission", api.Calls);
        api.AssertBalanced();
    }

    [Fact]
    public void SuccessfulNullCreationIsAPointerFailure()
    {
        var api = new Api { NullManager = true };
        using SensorController controller = Create(api);
        Assert.Equal(unchecked((int)0x80004003), Assert.Throws<COMException>(() => controller.Refresh()).HResult);
        api.AssertBalanced();
    }

    [Fact]
    public void ManagedCreationFailureAfterOutputReleasesTheManager()
    {
        var api = new Api();
        var failure = new InvalidOperationException("生成後の中断");
        api.OnOperation = call =>
        {
            if (call == "create")
            {
                throw failure;
            }
        };
        using SensorController controller = Create(api);
        Assert.Same(failure, Record.Exception(() => controller.Refresh()));
        api.AssertBalanced();
    }

    [Fact]
    public void FailedManagerRegistrationReleasesTheFactoryAndConnectionReferences()
    {
        var api = new Api { SetFailure = 1 };
        using SensorController controller = Create(api);
        Assert.Equal(Api.Failure, Assert.ThrowsAny<Exception>(() => controller.Refresh()).HResult);
        Assert.DoesNotContain("permission", api.Calls);
        api.AssertBalanced();
    }

    [Fact]
    public void FailedDeviceRegistrationKeepsEarlierDevicesAndAllowsALaterRetry()
    {
        var api = new Api { SetFailure = 202 };
        using SensorController controller = Create(api);
        Assert.Equal(Api.Failure, Assert.ThrowsAny<Exception>(() => controller.Refresh()).HResult);
        Assert.Equal(1, api.References[202]);
        AssertValue(controller, 1.5);
        api.SetFailure = 0;
        Assert.Equal(0, api.Manager().OnSensorEnter(202, 0));
        Assert.Equal(3, api.References[202]);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void RemovedConnectionFailureRemainsAvailableToTheController()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        api.ClearFailure = 201;
        Guid id = Api.Id(201);
        Assert.Equal(Api.Failure, api.Device(201).OnLeave(in id));
        Assert.Equal(Api.Failure, controller.TakeCallbackFailure()!.HResult);
        Assert.Null(controller.TakeCallbackFailure());
        Assert.Equal(1, api.References[201]);
        AssertValue(controller, 2.5);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void DisposeClosesEverySinkAndReleasesOtherReferencesAfterOneDetachFails()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        controller.Refresh();
        SensorManagerEventSink manager = api.Manager();
        SensorEventSink device = api.Device(202);
        api.ClearFailure = 201;
        Assert.Single(Assert.Throws<AggregateException>(controller.Dispose).InnerExceptions);
        api.AssertBalanced();
        int calls = api.Calls.Count;
        Assert.Equal(0, manager.OnSensorEnter(201, 0));
        Assert.Equal(0, device.OnDataUpdated(202, 301));
        controller.Dispose();
        Assert.Equal(calls, api.Calls.Count);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("set:1")]
    [InlineData("set:201")]
    public void ReentrantDisposalCancelsPendingCreationAndConnections(string operation)
    {
        var api = new Api();
        using SensorController controller = Create(api);
        api.OnOperation = call =>
        {
            if (call == operation)
            {
                controller.Dispose();
            }
        };
        Assert.Throws<ObjectDisposedException>(() => controller.Refresh());
        api.AssertBalanced();
    }

    [Fact]
    public void LeaveDuringRegistrationDoesNotCommitAClosedConnection()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        api.OnOperation = call =>
        {
            if (call == "set:201")
            {
                api.OnOperation = null;
                Guid id = Api.Id(201);
                Assert.Equal(0, api.Device(201).OnLeave(in id));
            }
        };
        Assert.Equal(0, controller.Refresh());
        Assert.Equal(1, api.References[201]);
        AssertValue(controller, 2.5);
        Assert.Equal(0, api.Manager().OnSensorEnter(201, 0));
        Assert.Equal(3, api.References[201]);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData("category:201")]
    [InlineData("set:201")]
    public void ReentrantEntryForTheSameIdDoesNotDuplicateItsConnection(string operation)
    {
        var api = new Api();
        using SensorController controller = Create(api);
        api.OnOperation = call =>
        {
            if (call == operation)
            {
                api.OnOperation = null;
                Assert.Equal(0, api.Manager().OnSensorEnter(201, 0));
            }
        };
        Assert.Equal(0, controller.Refresh());
        Assert.Equal(1, api.Calls.Count(call => call == "set:201"));
        Assert.Equal(3, api.References[201]);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void RecursiveRefreshIsReportedWithoutChangingTheActiveEnumeration()
    {
        var api = new Api();
        SensorController? current = null;
        using SensorController controller = Create(api, () => current!.Refresh());
        current = controller;
        api.OnOperation = call =>
        {
            if (call == "set:201")
            {
                api.OnOperation = null;
                Assert.True(api.Device(201).OnDataUpdated(201, 301) < 0);
            }
        };
        Assert.Equal(0, controller.Refresh());
        Assert.IsType<InvalidOperationException>(controller.TakeCallbackFailure());
        Assert.Equal(1, api.Creates);
        AssertValue(controller, 1.5);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void CategoriesWithoutReadableFieldsDoNotCreateConnections()
    {
        var api = new Api { Unsupported = 202 };
        using SensorController controller = Create(api);
        Assert.Equal(0, controller.Refresh());
        Assert.DoesNotContain("set:202", api.Calls);
        Assert.Equal(1, api.References[202]);
        controller.Dispose();
        api.AssertBalanced();
    }

    [Fact]
    public void WrongThreadDoesNotTouchNativeState()
    {
        var api = new Api();
        using SensorController controller = Create(api);
        var errors = new List<Exception?>();
        var thread = new Thread(() =>
        {
            errors.Add(Record.Exception(() => controller.Refresh()));
            errors.Add(Record.Exception(() => controller.TryRead(Field(), out _)));
            errors.Add(Record.Exception(controller.Dispose));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.All(errors, error => Assert.IsType<InvalidOperationException>(error));
        Assert.Empty(api.Calls);
    }

    private static SensorController Create(Api api, Action? updated = null) =>
        new(api, api, api, api, updated: updated);

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private static void AssertValue(SensorController controller, double expected)
    {
        Assert.True(controller.TryRead(Field(), out object? value));
        Assert.Equal(expected, value);
    }

    internal sealed class Api : ISensorManagerFactory, ISensorManagerNativeApi,
        ISensorEventNativeApi, ISensorReportNativeApi
    {
        internal const int Failure = unchecked((int)0x80070005);
        internal int Creates;
        internal int CreateResult;
        internal int DataResult;
        internal Guid? CategoryOverride;
        internal List<string> DeviceCalls { get; } = [];
        internal bool NullManager;
        internal nint SetFailure;
        internal nint ClearFailure;
        internal nint Unsupported;
        internal nint[] Items = [201, 202];
        internal Action<string>? OnOperation;
        internal List<string> Calls { get; } = [];
        internal Dictionary<nint, int> References
        {
            get;
        } = new()
        {
            [100] = 0,
            [101] = 0,
            [201] = 1,
            [202] = 1,
            [301] = 1,
            [401] = 0,
            [402] = 0
        };
        private readonly Dictionary<nint, SensorCallbackSink> sinks = [];
        private readonly Dictionary<nint, nint> installed = [];
        private int collections;
        private nint nextSink = 10000;

        internal static Guid Id(nint sensor) => new((int)sensor, 0, 0, new byte[8]);

        internal SensorManagerEventSink Manager() => (SensorManagerEventSink)sinks[installed[Creates]];

        internal bool HasDevice(nint sensor) => installed.ContainsKey(sensor);

        internal SensorEventSink Device(nint sensor) => (SensorEventSink)sinks[installed[sensor]];

        internal void AssertBalanced()
        {
            foreach (KeyValuePair<nint, int> pair in References)
            {
                Assert.Equal(pair.Key is 201 or 202 or 301 ? 1 : 0, pair.Value);
            }
        }

        public int Create(out nint manager)
        {
            collections = 0;
            Creates++;
            manager = NullManager ? 0 : Creates;
            if (manager != 0)
            {
                References[manager] = 1;
            }

            Record("create");
            return CreateResult;
        }

        public void AddRef(nint instance)
        {
            Assert.True(References[instance] > 0);
            References[instance]++;
            Record($"add:{instance}");
        }

        public void Release(nint instance)
        {
            Assert.True(References[instance] > 0);
            References[instance]--;
            Record($"release:{instance}");
        }

        public int GetSensorsByCategory(nint manager, in Guid category, out nint collection)
        {
            Assert.Equal(SensorManagerDiscovery.AllCategories, category);
            collection = 100 + collections++;
            References[collection]++;
            Record("collection");
            return 0;
        }

        public int RequestPermissions(nint manager, nint parent, nint collection, bool modal)
        {
            Assert.Equal(0, parent);
            Assert.True(modal);
            Record("permission");
            return 0;
        }

        public int GetCount(nint collection, out uint count)
        {
            count = (uint)Items.Length;
            return 0;
        }

        public int GetAt(nint collection, uint index, out nint sensor)
        {
            sensor = Items[index];
            References[sensor]++;
            return 0;
        }

        public int GetId(nint sensor, out Guid id)
        {
            id = Id(sensor);
            Record($"id:{sensor}");
            return 0;
        }

        public int GetCategory(nint sensor, out Guid category)
        {
            category = sensor == Unsupported ? Guid.Empty : CategoryOverride ?? Field().CategoryId;
            Record($"category:{sensor}");
            return 0;
        }

        public nint GetSinkPointer(SensorCallbackSink sink)
        {
            nint pointer = nextSink++;
            sinks.Add(pointer, sink);
            References.Add(pointer, 1);
            return pointer;
        }

        public int SetSink(nint source, nint sink, bool manager)
        {
            Assert.True(References[source] > 0);
            if (!manager)
            {
                DeviceCalls.Add($"{(sink == 0 ? "stop" : "start")}:{source}");
            }

            if (sink != 0)
            {
                References[sink]++;
            }

            if (installed.Remove(source, out nint previous))
            {
                References[previous]--;
            }

            if (sink != 0)
            {
                installed.Add(source, sink);
            }

            Record($"set:{source}");
            return sink != 0 && SetFailure == source || sink == 0 && ClearFailure == source
                ? Failure : 0;
        }

        public int GetData(nint sensor, out nint report)
        {
            report = sensor + 200;
            References[report]++;
            DeviceCalls.Add($"read:{sensor}");
            OnOperation?.Invoke($"data:{sensor}");
            return DataResult;
        }

        public int GetSensorValue(nint report, in SensorPropertyKey key, nint value)
        {
            double number = report == 301 ? 10.5 : report == 401 ? 1.5 : 2.5;
            Marshal.WriteInt16(value, (short)VarEnum.VT_R8);
            Marshal.WriteInt64(value, 8, BitConverter.DoubleToInt64Bits(number));
            return 0;
        }

        private void Record(string operation)
        {
            Calls.Add(operation);
            OnOperation?.Invoke(operation);
        }
    }
}
