using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

// 実managerとmessage pumpを使う機器不在の検証はSandboxだけで実行します。
[Trait("Category", "SandboxOnly")]
public sealed class SensorManagerIsolationTests(ITestOutputHelper output)
{
    private const int NotFound = unchecked((int)0x80070490);

    [Fact]
    public async Task SensorlessManagerPreservesAbsenceAndReplacesItsConnection()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(Directory.Exists(@"C:\ui-input"));
        RecordDpi("caller");
        var api = new ObservedApi(output);
        var apartment = new Apartment(output);
        await using var session = new SensorSession(() =>
        {
            api.Owner = Environment.CurrentManagedThreadId;
            return new SensorController(api, api, api);
        }, apartment);
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        for (int index = 0; index < 2; index++)
        {
            SensorQueryResult result = await session.QueryAsync(field)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            output.WriteLine($"query={index}; discovery=0x{result.DiscoveryResult:x8}; has-value={result.HasValue}; value-null={result.Value is null}");
            // この受入対象は機器不在です。別のエラーを不在とみなしません。
            Assert.Equal(NotFound, result.DiscoveryResult);
            Assert.False(result.HasValue);
            Assert.Null(result.Value);
        }

        Assert.Null(await session.TakeCallbackFailureAsync());
        await session.DisposeAsync();
        Assert.Equal(new[]
        {
            "create", "attach", "enumerate", "detach",
            "create", "attach", "enumerate", "detach"
        }, api.Calls);
        Assert.All(api.Balances.Values, balance => Assert.Equal(0, balance));
        Assert.Equal(api.Owner, apartment.InitializedThread);
        Assert.Equal(api.Owner, apartment.UninitializedThread);
        Assert.Equal(1, apartment.Uninitializes);
        output.WriteLine($"owner={api.Owner}; creates=2; attach=2; enumeration=2; detach=2; tracked-balances=zero; permission-native-calls=0; init=0x{apartment.InitializeResult:x8}; uninitialize=1");
    }

    private void RecordDpi(string role)
    {
        uint dpi = GetDpiForSystem();
        int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        output.WriteLine($"{role}-dpi-api={dpi}; {role}-awareness={awareness}");
        Assert.True(dpi > 0);
        Assert.InRange(awareness, 0, 2);
    }

    private sealed class ObservedApi(ITestOutputHelper output)
        : ISensorManagerFactory, ISensorManagerNativeApi, ISensorEventNativeApi
    {
        internal int Owner;
        internal List<string> Calls { get; } = [];
        internal Dictionary<nint, int> Balances { get; } = [];

        private void Verify()
        {
            Assert.Equal(Owner, Environment.CurrentManagedThreadId);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        }

        private void Acquire(nint pointer)
        {
            if (pointer != 0)
            {
                Balances[pointer] = Balances.GetValueOrDefault(pointer) + 1;
            }
        }

        public int Create(out nint manager)
        {
            Verify();
            Calls.Add("create");
            int result = SensorManagerFactory.Instance.Create(out manager);
            Acquire(manager);
            output.WriteLine($"create=0x{result:x8}; nonnull={manager != 0}");
            return result;
        }

        public void AddRef(nint instance)
        {
            Verify();
            Marshal.AddRef(instance);
            Acquire(instance);
        }

        public void Release(nint instance)
        {
            Verify();
            Assert.True(Balances.GetValueOrDefault(instance) > 0);
            Marshal.Release(instance);
            Balances[instance]--;
        }

        public nint GetSinkPointer(SensorCallbackSink sink)
        {
            Verify();
            nint pointer = SensorEventNativeApi.Instance.GetSinkPointer(sink);
            Acquire(pointer);
            return pointer;
        }

        public int SetSink(nint source, nint sink, bool manager)
        {
            Verify();
            Assert.True(manager);
            string action = sink == 0 ? "detach" : "attach";
            Calls.Add(action);
            int result = SensorEventNativeApi.Instance.SetSink(source, sink, manager);
            output.WriteLine($"{action}=0x{result:x8}");
            return result;
        }

        public int GetSensorsByCategory(nint manager, in Guid category, out nint collection)
        {
            Verify();
            Calls.Add("enumerate");
            Assert.Equal(SensorManagerDiscovery.AllCategories, category);
            int result = SensorManagerNativeApi.Instance.GetSensorsByCategory(
                manager, in category, out collection);
            Acquire(collection);
            output.WriteLine($"enumerate=0x{result:x8}; collection-nonnull={collection != 0}");
            return result;
        }

        public int RequestPermissions(nint manager, nint parent, nint collection, bool modal)
        {
            Verify();
            // 機器不在なら到達しません。環境が変わっても権限要求へ進めません。
            throw new InvalidOperationException("機器不在の検証中に権限要求へ到達しました。");
        }

        public int GetCount(nint collection, out uint count)
        {
            Verify();
            return SensorManagerNativeApi.Instance.GetCount(collection, out count);
        }

        public int GetAt(nint collection, uint index, out nint sensor)
        {
            Verify();
            int result = SensorManagerNativeApi.Instance.GetAt(collection, index, out sensor);
            Acquire(sensor);
            return result;
        }

        public int GetId(nint sensor, out Guid id)
        {
            Verify();
            return SensorManagerNativeApi.Instance.GetId(sensor, out id);
        }

        public int GetCategory(nint sensor, out Guid category)
        {
            Verify();
            return SensorManagerNativeApi.Instance.GetCategory(sensor, out category);
        }
    }

    private sealed class Apartment(ITestOutputHelper output) : ISensorApartment
    {
        internal int InitializeResult;
        internal int InitializedThread;
        internal int UninitializedThread;
        internal int Uninitializes;

        public int Initialize()
        {
            InitializedThread = Environment.CurrentManagedThreadId;
            uint dpi = GetDpiForSystem();
            int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            output.WriteLine($"owner-dpi-api={dpi}; owner-awareness={awareness}");
            Assert.True(dpi > 0);
            Assert.InRange(awareness, 0, 2);
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

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
}
