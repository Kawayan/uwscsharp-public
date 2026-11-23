using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllBufferedCallTests
{
    [Fact]
    public void ReentrantDisposeRetainsAllResourcesThroughResultCopy()
    {
        var allocator = new Allocator();
        using var first = NativeDllBuffer.Allocate(4, allocator);
        using var second = NativeDllBuffer.Allocate(4, allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        loader.Callback = (left, right) =>
        {
            first.Dispose();
            second.Dispose();
            module.Dispose();
            Marshal.WriteInt32(left, 12);
            Marshal.WriteInt32(right, 30);
            return 7;
        };
        int result = loader.Resolve(module).InvokeWithBuffers([first, second],
            pointers => [pointers[0], pointers[1]], (nativeResult, pointers) =>
            {
                Assert.Equal(7, nativeResult);
                Assert.Equal(0, allocator.Frees);
                Assert.Equal(0, loader.Frees);
                return Marshal.ReadInt32(pointers[0]) + Marshal.ReadInt32(pointers[1]);
            });
        Assert.Equal(42, result);
        Assert.Equal(2, allocator.Frees);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void FailureToRetainLaterBufferDoesNotPrepareOrInvoke()
    {
        var allocator = new Allocator();
        using var first = NativeDllBuffer.Allocate(4, allocator);
        using var second = NativeDllBuffer.Allocate(4, allocator);
        second.Dispose();
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        bool prepared = false;
        Assert.Throws<ObjectDisposedException>(() => loader.Resolve(module).InvokeWithBuffers(
            [first, second], pointers =>
            {
                prepared = true;
                return [pointers[0], pointers[1]];
            }, (_, _) => 0));
        first.Dispose();
        Assert.False(prepared);
        Assert.Equal(0, loader.Calls);
        Assert.Equal(2, allocator.Frees);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreparationOrResultFailureReleasesEveryRetention(bool failPreparation)
    {
        var allocator = new Allocator();
        using var buffer = NativeDllBuffer.Allocate(4, allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        Assert.Throws<InvalidOperationException>(() => loader.Resolve(module).InvokeWithBuffers<int>(
            [buffer], pointers =>
            {
                if (failPreparation)
                {
                    buffer.Dispose();
                    throw new InvalidOperationException();
                }

                return [pointers[0], pointers[0]];
            }, (_, _) =>
            {
                buffer.Dispose();
                module.Dispose();
                Assert.Equal(0, loader.Frees);
                throw new InvalidOperationException();
            }));
        Assert.Equal(failPreparation ? 0 : 1, loader.Calls);
        Assert.Equal(1, allocator.Frees);
        module.Dispose();
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void DuplicateBuffersAndMutatedInputListKeepOriginalOwnership()
    {
        var allocator = new Allocator();
        using var buffer = NativeDllBuffer.Allocate(4, allocator);
        using var replacement = NativeDllBuffer.Allocate(4, allocator);
        NativeDllBuffer[] inputs = [buffer, buffer];
        int result = NativeDllBuffer.WithPointers(inputs, pointers =>
        {
            inputs[0] = replacement;
            buffer.Dispose();
            Assert.Equal(pointers[0], pointers[1]);
            Assert.Equal(0, allocator.Frees);
            Marshal.WriteInt32(pointers[0], 42);
            return Marshal.ReadInt32(pointers[1]);
        });
        Assert.Equal(42, result);
        Assert.Equal(1, allocator.Frees);
        replacement.Dispose();
        Assert.Equal(2, allocator.Frees);
    }

    [Fact]
    public void InvalidAbiArgumentsReleaseBuffersWithoutReadingResults()
    {
        var allocator = new Allocator();
        using var buffer = NativeDllBuffer.Allocate(4, allocator);
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        bool read = false;
        Assert.Throws<ArgumentException>(() => loader.Resolve(module).InvokeWithBuffers(
            [buffer], pointers =>
            {
                buffer.Dispose();
                return [pointers[0], 0];
            }, (_, _) =>
            {
                read = true;
                return 0;
            }));
        Assert.False(read);
        Assert.Equal(0, loader.Calls);
        Assert.Equal(1, allocator.Frees);
        module.Dispose();
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public void ManyBufferReferencesDoNotRequireRecursiveCallbacks()
    {
        var allocator = new Allocator();
        using var buffer = NativeDllBuffer.Allocate(1, allocator);
        NativeDllBuffer[] buffers = [.. Enumerable.Repeat(buffer, 10000)];
        Assert.Equal(10000, NativeDllBuffer.WithPointers(buffers, pointers =>
        {
            buffer.Dispose();
            Assert.Equal(0, allocator.Frees);
            return pointers.Count;
        }));
        Assert.Equal(1, allocator.Frees);
    }

    [Fact]
    public void NoBuffersStillCopiesReturnBeforeReleasingModule()
    {
        using var loader = new Loader();
        using NativeDllModule module = loader.Open();
        int result = loader.Resolve(module).InvokeWithBuffers([], _ => [(nint)0, (nint)0],
            (value, pointers) =>
            {
                module.Dispose();
                Assert.Empty(pointers);
                Assert.Equal(0, loader.Frees);
                return (int)value!;
            });
        Assert.Equal(7, result);
        Assert.Equal(1, loader.Frees);
    }

    private sealed class Allocator : INativeDllBufferAllocator
    {
        internal int Frees;

        public nint Allocate(int length) => Marshal.AllocHGlobal(length);

        public void Free(nint address)
        {
            Frees++;
            Marshal.FreeHGlobal(address);
        }
    }

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeCall native;
        internal Func<nint, nint, int> Callback = (_, _) => 7;
        internal int Calls;
        internal int Frees;

        internal Loader()
        {
            native = (left, right) =>
            {
                Calls++;
                return Callback(left, right);
            };
        }

        internal NativeDllModule Open() =>
            NativeDllModule.Load(Path.Combine(Path.GetTempPath(), "buffered-call.dll"), this);

        internal NativeDllModule.Function Resolve(NativeDllModule module) =>
            module.Resolve("Call", new NativeDllCallSite(typeof(int), typeof(nint), typeof(nint)));

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) => Marshal.GetFunctionPointerForDelegate(native);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(native);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NativeCall(nint left, nint right);
    }
}
