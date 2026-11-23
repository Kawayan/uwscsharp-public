using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 列挙と権限要求を偽APIへ送り、実COM・機器・ダイアログは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorManagerDiscoveryTests
{
    [Fact]
    public void PermissionCollectionIsReplacedBeforeOrderedRegistration()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        Assert.Equal(0, SensorManagerDiscovery.Refresh(1, store, api));
        Assert.Equal(["add:1", "collection:10", "permission:10", "release:10",
            "collection:11", "count", "at:0", "id:101", "category:101", "add:101",
            "release:101", "at:1", "id:102", "category:102", "add:102",
            "release:102", "release:11", "release:1"], api.Calls);
        Assert.True(store.TryRead(Field(), out object? value));
        Assert.Equal(101d, value);
        Assert.Equal(1, api.References[1]);
        Assert.Equal(0, api.References[10]);
        Assert.Equal(0, api.References[11]);
        Assert.Equal(2, api.References[101]);
        Assert.Equal(2, api.References[102]);
    }

    [Fact]
    public void EmptyEnumerationDoesNotRemoveExistingDevices()
    {
        var api = new Api { Items = [] };
        using var store = new SensorDeviceStore(api);
        store.Register(Api.Id(101), Field().CategoryId, 101);
        Assert.Equal(0, SensorManagerDiscovery.Refresh(1, store, api));
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("at:", StringComparison.Ordinal));
        Assert.True(store.TryRead(Field(), out object? value));
        Assert.Equal(101d, value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedCategoryLookupReturnsItsHResultAndReleasesPartialOutput(int phase)
    {
        const int failure = unchecked((int)0x80070490);
        var api = new Api { Failure = $"collection:{phase + 9}", Result = failure };
        using var store = new SensorDeviceStore(api);
        store.Register(Api.Id(101), Field().CategoryId, 101);
        Assert.Equal(failure, SensorManagerDiscovery.Refresh(1, store, api));
        Assert.Equal(phase, api.CollectionCalls);
        Assert.Equal(phase == 2, api.Calls.Contains("permission:10"));
        Assert.DoesNotContain("count", api.Calls);
        Assert.True(store.Contains(Api.Id(101)));
        store.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x800704c7))]
    public void PermissionDenialAndCancellationThrowAndStopBeforeReenumeration(int result)
    {
        var api = new Api { Failure = "permission:10", Result = result };
        using var store = new SensorDeviceStore(api);
        Assert.Equal(result, Assert.ThrowsAny<Exception>(() =>
            SensorManagerDiscovery.Refresh(1, store, api)).HResult);
        Assert.Equal(1, api.CollectionCalls);
        api.AssertBalanced();
    }

    [Theory]
    [InlineData("count")]
    [InlineData("at:1")]
    [InlineData("id:102")]
    [InlineData("category:102")]
    public void EntryFailureKeepsEarlierRegistrationsAndReleasesTemporaryReferences(string operation)
    {
        var api = new Api { Failure = operation };
        using var store = new SensorDeviceStore(api);
        Assert.Equal(api.Result, Assert.ThrowsAny<Exception>(() =>
            SensorManagerDiscovery.Refresh(1, store, api)).HResult);
        Assert.Equal(operation != "count", store.Contains(Api.Id(101)));
        Assert.False(store.Contains(Api.Id(102)));
        store.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData("collection:10")]
    [InlineData("collection:11")]
    [InlineData("at:0")]
    public void SuccessfulNullOutputIsNotAnEmptyEnumeration(string operation)
    {
        var api = new Api { NullOutput = operation };
        using var store = new SensorDeviceStore(api);
        Assert.Equal(unchecked((int)0x80004003), Assert.Throws<COMException>(() =>
            SensorManagerDiscovery.Refresh(1, store, api)).HResult);
        api.AssertBalanced();
    }

    [Fact]
    public void ManagedFailureAfterGetAtOutputStillReleasesTheOutput()
    {
        var failure = new InvalidOperationException("取得後の中断");
        var api = new Api
        {
            OnOperation = call =>
        {
            if (call == "at:0")
            {
                throw failure;
            }
        }
        };
        using var store = new SensorDeviceStore(api);
        Assert.Same(failure, Record.Exception(() => SensorManagerDiscovery.Refresh(1, store, api)));
        api.AssertBalanced();
    }

    [Fact]
    public void ExistingIdSkipsCategoryAndKeepsItsOriginalReferenceAndOrder()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        store.Register(Api.Id(101), Field().CategoryId, 101);
        api.Items = [102, 101];
        api.Failure = "category:101";
        Assert.Equal(0, SensorManagerDiscovery.Refresh(1, store, api));
        Assert.DoesNotContain("category:101", api.Calls);
        Assert.True(store.TryRead(Field(), out object? value));
        Assert.Equal(101d, value);
        store.Dispose();
        api.AssertBalanced();
    }

    [Theory]
    [InlineData("add:1")]
    [InlineData("collection:10")]
    [InlineData("permission:10")]
    [InlineData("count")]
    [InlineData("id:101")]
    [InlineData("category:101")]
    public void ReentrantDisposalStopsDiscoveryAndReleasesTemporaryReferences(string operation)
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        api.OnOperation = call =>
        {
            if (call == operation)
            {
                store.Dispose();
            }
        };
        Assert.Throws<ObjectDisposedException>(() => SensorManagerDiscovery.Refresh(1, store, api));
        api.AssertBalanced();
        Assert.DoesNotContain("at:1", api.Calls);
    }

    [Fact]
    public void PositiveSuccessCodesStillPerformPermissionAndRegistration()
    {
        var api = new Api { Success = 1 };
        using var store = new SensorDeviceStore(api);
        Assert.Equal(1, SensorManagerDiscovery.Refresh(1, store, api));
        Assert.True(store.Contains(Api.Id(101)));
        Assert.True(store.Contains(Api.Id(102)));
    }

    [Fact]
    public void InvalidCallerStateDoesNotTouchAnyNativeReference()
    {
        var api = new Api();
        using var store = new SensorDeviceStore(api);
        Assert.Throws<ArgumentException>(() => SensorManagerDiscovery.Refresh(0, store, api));
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() =>
            SensorManagerDiscovery.Refresh(1, store, api)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(error);
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SensorManagerDiscovery.Refresh(1, store, api));
        Assert.Empty(api.Calls);
    }

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private sealed class Api : ISensorManagerNativeApi, ISensorReportNativeApi
    {
        internal nint[] Items = [101, 102];
        internal string? Failure;
        internal string? NullOutput;
        internal int Result = unchecked((int)0x80004005);
        internal int Success;
        internal int CollectionCalls;
        internal Action<string>? OnOperation;
        internal List<string> Calls { get; } = [];
        internal Dictionary<nint, int> References
        {
            get;
        } = new()
        {
            [1] = 1,
            [10] = 0,
            [11] = 0,
            [101] = 1,
            [102] = 1,
            [1101] = 0,
            [1102] = 0
        };

        internal static Guid Id(nint sensor) => new((int)sensor, 0, 0, new byte[8]);

        internal void AssertBalanced()
        {
            foreach (KeyValuePair<nint, int> reference in References)
            {
                int expected = reference.Key is 1 or 101 or 102 ? 1 : 0;
                Assert.Equal(expected, reference.Value);
            }
        }

        public void AddRef(nint instance)
        {
            Assert.True(References[instance] > 0);
            References[instance]++;
            Complete($"add:{instance}");
        }

        public void Release(nint instance)
        {
            Assert.True(References[instance] > 0);
            References[instance]--;
            Complete($"release:{instance}");
        }

        public int GetSensorsByCategory(nint manager, in Guid category, out nint collection)
        {
            Assert.Equal(1, manager);
            Assert.True(References[manager] > 1);
            Assert.Equal(SensorManagerDiscovery.AllCategories, category);
            nint next = 10 + CollectionCalls++;
            string operation = $"collection:{next}";
            collection = NullOutput == operation ? 0 : next;
            if (collection != 0)
            {
                References[collection]++;
            }

            return Complete(operation);
        }

        public int RequestPermissions(nint manager, nint parent, nint collection, bool modal)
        {
            Assert.Equal(1, manager);
            Assert.Equal(0, parent);
            Assert.True(modal);
            Assert.True(References[collection] > 0);
            return Complete($"permission:{collection}");
        }

        public int GetCount(nint collection, out uint count)
        {
            Assert.Equal(11, collection);
            Assert.Equal(0, References[10]);
            count = (uint)Items.Length;
            return Complete("count");
        }

        public int GetAt(nint collection, uint index, out nint sensor)
        {
            Assert.Equal(11, collection);
            Assert.True(References[collection] > 0);
            string operation = $"at:{index}";
            sensor = NullOutput == operation ? 0 : Items[index];
            if (sensor != 0)
            {
                References[sensor]++;
            }

            return Complete(operation);
        }

        public int GetId(nint sensor, out Guid id)
        {
            id = Id(sensor);
            return Complete($"id:{sensor}");
        }

        public int GetCategory(nint sensor, out Guid category)
        {
            category = Field().CategoryId;
            return Complete($"category:{sensor}");
        }

        public int GetData(nint sensor, out nint report)
        {
            report = sensor + 1000;
            References[report]++;
            return 0;
        }

        public int GetSensorValue(nint report, in SensorPropertyKey key, nint propertyValue)
        {
            Marshal.WriteInt16(propertyValue, (short)VarEnum.VT_R8);
            Marshal.WriteInt64(propertyValue, 8, BitConverter.DoubleToInt64Bits((double)(report - 1000)));
            return 0;
        }

        private int Complete(string operation)
        {
            Calls.Add(operation);
            OnOperation?.Invoke(operation);
            return Failure == operation ? Result : Success;
        }
    }
}
