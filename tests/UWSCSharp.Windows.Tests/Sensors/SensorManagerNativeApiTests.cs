using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するvtableを呼び、実COMの列挙・権限要求・GUIは使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorManagerNativeApiTests
{
    private static readonly ConcurrentDictionary<nint, Fixture> Instances = new();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80070005))]
    public void SlotsPreserveGuidUnsignedWidthsBoolAndHResult(int result)
    {
        using var fixture = new Fixture { Result = result };
        SensorManagerNativeApi api = SensorManagerNativeApi.Instance;
        Guid requested = new("c317c286-c468-4288-9975-d4c4587c442c");
        api.AddRef(fixture.Manager);
        Assert.Equal(result, api.GetSensorsByCategory(fixture.Manager, in requested, out nint collection));
        Assert.Equal(fixture.Collection, collection);
        Assert.Equal(requested, fixture.CategoryRequest);
        Assert.Equal(result, api.RequestPermissions(fixture.Manager, 0, collection, true));
        Assert.Equal(1, fixture.Modal);
        Assert.Equal(0, fixture.Parent);
        Assert.Equal(result, api.RequestPermissions(fixture.Manager, 0x12345678, collection, false));
        Assert.Equal(0, fixture.Modal);
        Assert.Equal(0x12345678, fixture.Parent);
        Assert.Equal(result, api.GetCount(collection, out uint count));
        Assert.Equal(0xfedcba98u, count);
        Assert.Equal(result, api.GetAt(collection, 0x87654321, out nint sensor));
        Assert.Equal(0x87654321u, fixture.Index);
        Assert.Equal(fixture.Sensor, sensor);
        Assert.Equal(result, api.GetId(sensor, out Guid id));
        Assert.Equal(fixture.Id, id);
        Assert.Equal(result, api.GetCategory(sensor, out Guid category));
        Assert.Equal(fixture.Category, category);
        api.Release(sensor);
        api.Release(collection);
        api.Release(fixture.Manager);
        Assert.All(fixture.References.Values, references => Assert.Equal(1, references));
        Assert.Equal(["add", "collection", "permission", "permission", "count",
            "at", "id", "category", "release", "release", "release"], fixture.Calls);
        Assert.All(fixture.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Fact]
    public void NullInterfacesAreRejectedBeforeReadingTheirVtables()
    {
        SensorManagerNativeApi api = SensorManagerNativeApi.Instance;
        Guid category = Guid.Empty;
        Assert.Throws<ArgumentException>(() => api.GetSensorsByCategory(0, in category, out _));
        Assert.Throws<ArgumentException>(() => api.RequestPermissions(0, 0, 0, true));
        Assert.Throws<ArgumentException>(() => api.GetCount(0, out _));
        Assert.Throws<ArgumentException>(() => api.GetAt(0, 0, out _));
        Assert.Throws<ArgumentException>(() => api.GetId(0, out _));
        Assert.Throws<ArgumentException>(() => api.GetCategory(0, out _));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint instance)
    {
        Fixture fixture = Instances[instance];
        fixture.Record("add");
        return (uint)++fixture.References[instance];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint instance)
    {
        Fixture fixture = Instances[instance];
        fixture.Record("release");
        return (uint)--fixture.References[instance];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCollection(nint instance, nint category, nint collection)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Manager ? "collection" : "wrong-this");
        fixture.CategoryRequest = Marshal.PtrToStructure<Guid>(category);
        fixture.References[fixture.Collection]++;
        Marshal.WriteIntPtr(collection, fixture.Collection);
        return fixture.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RequestPermissions(nint instance, nint parent, nint collection, int modal)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Manager && collection == fixture.Collection
            ? "permission" : "wrong-this");
        fixture.Parent = parent;
        fixture.Modal = modal;
        return fixture.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCount(nint instance, nint count)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Collection ? "count" : "wrong-this");
        Marshal.WriteInt32(count, unchecked((int)0xfedcba98));
        return fixture.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetAt(nint instance, uint index, nint sensor)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Collection ? "at" : "wrong-this");
        fixture.Index = index;
        fixture.References[fixture.Sensor]++;
        Marshal.WriteIntPtr(sensor, fixture.Sensor);
        return fixture.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetId(nint instance, nint output)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Sensor ? "id" : "wrong-this");
        Marshal.StructureToPtr(fixture.Id, output, false);
        return fixture.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCategory(nint instance, nint output)
    {
        Fixture fixture = Instances[instance];
        fixture.Record(instance == fixture.Sensor ? "category" : "wrong-this");
        Marshal.StructureToPtr(fixture.Category, output, false);
        return fixture.Result;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly nint Manager;
        internal readonly nint Collection;
        internal readonly nint Sensor;
        internal readonly Guid Id = new("11223344-5566-7788-99aa-bbccddeeff00");
        internal readonly Guid Category = new("17a665c0-9063-4216-b202-5c7a255e18ce");
        internal int Result;
        internal Guid CategoryRequest;
        internal nint Parent;
        internal int Modal;
        internal uint Index;
        internal Dictionary<nint, int> References { get; } = [];
        internal List<string> Calls { get; } = [];
        internal List<int> Threads { get; } = [];
        private readonly List<nint> allocations = [];

        internal unsafe Fixture()
        {
            nint managerTable = Allocate(8 * IntPtr.Size);
            nint collectionTable = Allocate(5 * IntPtr.Size);
            nint sensorTable = Allocate(5 * IntPtr.Size);
            Manager = Create(managerTable);
            Collection = Create(collectionTable);
            Sensor = Create(sensorTable);
            foreach (nint table in new[] { managerTable, collectionTable, sensorTable })
            {
                Marshal.WriteIntPtr(table, IntPtr.Size,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef);
                Marshal.WriteIntPtr(table, 2 * IntPtr.Size,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release);
            }

            Marshal.WriteIntPtr(managerTable, 3 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&GetCollection);
            Marshal.WriteIntPtr(managerTable, 7 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int, int>)&RequestPermissions);
            Marshal.WriteIntPtr(collectionTable, 3 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int>)&GetAt);
            Marshal.WriteIntPtr(collectionTable, 4 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetCount);
            Marshal.WriteIntPtr(sensorTable, 3 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetId);
            Marshal.WriteIntPtr(sensorTable, 4 * IntPtr.Size,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&GetCategory);
        }

        internal void Record(string call)
        {
            Calls.Add(call);
            Threads.Add(Environment.CurrentManagedThreadId);
        }

        private nint Create(nint table)
        {
            nint instance = Allocate(IntPtr.Size);
            Marshal.WriteIntPtr(instance, table);
            References.Add(instance, 1);
            Instances.TryAdd(instance, this);
            return instance;
        }

        private nint Allocate(int size)
        {
            nint pointer = Marshal.AllocHGlobal(size);
            allocations.Add(pointer);
            Marshal.Copy(new byte[size], 0, pointer, size);
            return pointer;
        }

        public void Dispose()
        {
            foreach (nint instance in References.Keys)
            {
                Instances.TryRemove(instance, out _);
            }

            foreach (nint pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
