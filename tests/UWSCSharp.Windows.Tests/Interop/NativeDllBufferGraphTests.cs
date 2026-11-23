using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllBufferGraphTests
{
    [Fact]
    public void NestedPointersAndAliasesRemainAliveThroughResultCopy()
    {
        var allocator = new Allocator();
        var layout = new NativeDllStructureLayout([typeof(byte), typeof(nint), typeof(nint)], true);
        var graph = new NativeDllBufferGraph(
            [layout.Encode([(byte)3, (nint)0, (nint)0]), new byte[nint.Size], new byte[8]],
            [new(0, layout.Offsets[1], 1), new(0, layout.Offsets[2], 2, 4), new(1, 0, 2, 4)], allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = root =>
        {
            nint child = Marshal.ReadIntPtr(root + layout.Offsets[1]);
            nint leaf = Marshal.ReadIntPtr(child);
            nint alias = Marshal.ReadIntPtr(root + layout.Offsets[2]);
            module.Dispose();
            Marshal.WriteInt32(leaf, 42);
            return leaf == alias ? 7 : -1;
        };
        byte[] result = graph.Invoke(loader.Resolve(module), pointers => [pointers[0]],
            (value, copies) =>
            {
                Assert.Equal(7, value);
                Assert.Equal(0, loader.Frees);
                Assert.Equal(0, allocator.Frees);
                Assert.Equal(42, NativeDllScalarCodec.Decode(typeof(int), copies[2].AsSpan(4)));
                return copies[2];
            });
        Assert.Equal(3, allocator.Frees);
        Assert.Equal(1, loader.Frees);
        Assert.Equal(42, BitConverter.ToInt32(result, 4));
    }

    [Fact]
    public void EachInvocationStartsFromCopiedInputAndLinks()
    {
        byte[] leaf = NativeDllScalarCodec.Encode(10);
        byte[][] bytes = [new byte[nint.Size], leaf];
        NativeDllBufferLink[] links = [new(0, 0, 1)];
        var graph = new NativeDllBufferGraph(bytes, links);
        Array.Clear(leaf);
        bytes[0] = [];
        links[0] = new(99, 0, 99);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = root =>
        {
            nint child = Marshal.ReadIntPtr(root);
            int value = Marshal.ReadInt32(child) + 1;
            Marshal.WriteInt32(child, value);
            return value;
        };
        for (int index = 0; index < 2; index++)
        {
            int value = graph.Invoke(loader.Resolve(module), pointers => [pointers[0]],
                (_, copies) => BitConverter.ToInt32(copies[1]));
            Assert.Equal(11, value);
        }
    }

    [Fact]
    public async Task ConcurrentInvocationsUseDifferentLiveRegions()
    {
        var graph = new NativeDllBufferGraph([NativeDllScalarCodec.Encode(10)], []);
        using var barrier = new Barrier(2);
        var addresses = new System.Collections.Concurrent.ConcurrentBag<nint>();
        var callbackResults = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var prepared = new System.Collections.Concurrent.ConcurrentDictionary<nint, int>();
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = root =>
        {
            addresses.Add(root);
            int thread = Environment.CurrentManagedThreadId;
            if (!prepared.TryGetValue(root, out int callerThread))
            {
                // 配送先が未確認のポインターは読み書きせず、診断して失敗させます。
                observations.Enqueue($"未登録root=0x{root:x}, thread={thread}");
                callbackResults.Enqueue(int.MinValue);
                return int.MinValue;
            }

            int beforeWait = Marshal.ReadInt32(root);
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                observations.Enqueue($"期限root=0x{root:x}, before={beforeWait}, "
                    + $"callerThread={callerThread}, callbackThread={thread}");
                callbackResults.Enqueue(-1);
                return -1;
            }

            int afterWait = Marshal.ReadInt32(root);
            int value = afterWait + 1;
            Marshal.WriteInt32(root, value);
            observations.Enqueue($"root=0x{root:x}, before={beforeWait}, after={afterWait}, "
                + $"written={Marshal.ReadInt32(root)}, callerThread={callerThread}, callbackThread={thread}");
            callbackResults.Enqueue(value);
            return value;
        };
        NativeDllModule.Function function = loader.Resolve(module);
        Task<int> first = Task.Run(Invoke);
        Task<int> second = Task.Run(Invoke);
        int[] results = await Task.WhenAll(first, second);
        Assert.Equal([11, 11], results);
        Assert.Equal(2, addresses.Distinct().Count());

        int Invoke()
        {
            nint sent = 0;
            int initial = int.MinValue;
            return graph.Invoke(function, pointers =>
            {
                sent = pointers[0];
                initial = Marshal.ReadInt32(sent);
                Assert.Equal(10, initial);
                Assert.True(prepared.TryAdd(sent, Environment.CurrentManagedThreadId),
                    $"呼出し前の領域が重複しました。address=0x{sent:x}");
                return [sent];
            }, (value, copies) =>
            {
                int copied = BitConverter.ToInt32(copies[0]);
                Assert.True(value is int returned && returned == 11,
                    $"送信=0x{sent:x}, 初期値={initial}, 戻り値={value}, コピー値={copied}, "
                    + $"callback=[{string.Join(", ", callbackResults)}], "
                    + $"観測=[{string.Join("; ", observations)}], runtime={Environment.Version}");
                return copied;
            });
        }
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(0, 0, 0, int.MaxValue)]
    public void InvalidLinksAreRejectedBeforeAllocation(int source, int offset, int target, int targetOffset)
    {
        var allocator = new Allocator();
        Assert.Throws<ArgumentException>(() => new NativeDllBufferGraph(
            [new byte[nint.Size]], [new(source, offset, target, targetOffset)], allocator));
        Assert.Equal(0, allocator.Allocations);
    }

    [Fact]
    public void OverlappingPointerSlotsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new NativeDllBufferGraph(
            [new byte[nint.Size * 2]], [new(0, 0, 0), new(0, nint.Size - 1, 0)]));
    }

    [Fact]
    public void SelfReferencesAndOnePastEndAreStoredWithoutFollowingThem()
    {
        int length = nint.Size * 2;
        var graph = new NativeDllBufferGraph([new byte[length]],
            [new(0, 0, 0), new(0, nint.Size, 0, length)]);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = root =>
            Marshal.ReadIntPtr(root) == root && Marshal.ReadIntPtr(root + nint.Size) == root + length ? 1 : 0;
        Assert.Equal(1, graph.Invoke(loader.Resolve(module), pointers => [pointers[0]], (value, _) => value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallbackFailureReleasesAllOwnedBuffers(bool failPreparation)
    {
        var allocator = new Allocator();
        var graph = new NativeDllBufferGraph([new byte[4], new byte[4]], [], allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        Assert.Throws<InvalidOperationException>(() => graph.Invoke<int>(loader.Resolve(module), pointers =>
        {
            if (failPreparation)
            {
                throw new InvalidOperationException();
            }

            return [pointers[0]];
        }, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(2, allocator.Frees);
        Assert.Equal(failPreparation ? 0 : 1, loader.Calls);
    }

    [Fact]
    public void AllocationFailureReleasesEarlierBuffersWithoutInvokingDll()
    {
        var allocator = new Allocator { FailAt = 2 };
        var graph = new NativeDllBufferGraph([new byte[4], new byte[4]], [], allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        Assert.Throws<OutOfMemoryException>(() => graph.Invoke(loader.Resolve(module),
            pointers => [pointers[0]], (value, _) => value));
        Assert.Equal(1, allocator.Frees);
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public void ReplacedNativePointerIsCopiedButNeverDereferenced()
    {
        var graph = new NativeDllBufferGraph([new byte[nint.Size], new byte[4]], [new(0, 0, 1)]);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = root =>
        {
            Marshal.WriteIntPtr(root, (nint)1);
            return 0;
        };
        graph.Invoke(loader.Resolve(module), pointers => [pointers[0]], (_, copies) =>
        {
            Assert.Equal((nint)1, NativeDllScalarCodec.Decode(typeof(nint), copies[0]));
            Assert.Equal(new byte[4], copies[1]);
            return 0;
        });
    }

    private sealed class Allocator : INativeDllBufferAllocator
    {
        internal int Allocations;
        internal int Frees;
        internal int FailAt;

        public nint Allocate(int length)
        {
            Allocations++;
            return Allocations == FailAt ? throw new OutOfMemoryException() : Marshal.AllocHGlobal(length);
        }

        public void Free(nint address)
        {
            Frees++;
            Marshal.FreeHGlobal(address);
        }
    }

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeCall native;
        internal Func<nint, int> Callback = _ => 7;
        internal int Calls;
        internal int Frees;

        internal Loader()
        {
            native = root =>
            {
                Interlocked.Increment(ref Calls);
                return Callback(root);
            };
        }

        internal NativeDllModule Open() =>
            NativeDllModule.Load(Path.Combine(Path.GetTempPath(), "buffer-graph.dll"), this);

        internal NativeDllModule.Function Resolve(NativeDllModule module) =>
            module.Resolve("Call", new NativeDllCallSite(typeof(int), typeof(nint)));

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) => Marshal.GetFunctionPointerForDelegate(native);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(native);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NativeCall(nint root);
    }
}
