using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllBufferTests
{
    [Fact]
    public void BufferIsZeroedAcrossChunksAndCopiesOwnedBytes()
    {
        using var buffer = NativeDllBuffer.Allocate(8193);
        Assert.All(buffer.Read(0, buffer.Length), value => Assert.Equal(0, value));
        buffer.Write(4095, [1, 2, 3]);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 0 }, buffer.Read(4094, 5));
        byte[] copy = buffer.Read(4095, 3);
        copy[0] = 9;
        Assert.Equal(1, buffer.Read(4095, 1)[0]);
    }

    [Fact]
    public void EmptyBufferHasStorageButNoWritablePayload()
    {
        using var buffer = NativeDllBuffer.Allocate(0);
        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.WithPointer(address => address != 0));
        Assert.Empty(buffer.Read(0, 0));
        buffer.Write(0, []);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(0, [1]));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(9, 0)]
    [InlineData(8, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    public void OutOfRangeReadsAreRejectedWithoutOverflow(int offset, int count)
    {
        using var buffer = NativeDllBuffer.Allocate(8);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Read(offset, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(8, [1]));
    }

    [Fact]
    public void ReentrantDisposeRetainsStorageAndRejectsNewOperations()
    {
        var allocator = new Allocator();
        var buffer = NativeDllBuffer.Allocate(8, allocator);
        int value = buffer.WithPointer(address =>
        {
            buffer.Dispose();
            Assert.Equal(0, allocator.Frees);
            Assert.Throws<ObjectDisposedException>(() => buffer.Read(0, 1));
            Marshal.WriteInt32(address, 42);
            return Marshal.ReadInt32(address);
        });
        Assert.Equal(42, value);
        Assert.Equal(1, allocator.Frees);
        buffer.Dispose();
        Assert.Equal(1, allocator.Frees);
        Assert.Throws<ObjectDisposedException>(() => buffer.Write(0, []));
    }

    [Fact]
    public async Task DisposeWaitsForTheLastConcurrentUserWithoutBlocking()
    {
        var allocator = new Allocator();
        using var buffer = NativeDllBuffer.Allocate(8, allocator);
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        Task<int> Start(ManualResetEventSlim release) => Task.Run(() => buffer.WithPointer(address =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                entered.TrySetResult();
            }

            return release.Wait(TimeSpan.FromSeconds(10)) ? Marshal.ReadInt32(address) : -1;
        }));
        Task<int> first = Start(firstRelease);
        Task<int> second = Start(secondRelease);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            buffer.Dispose();
            Assert.Equal(0, allocator.Frees);
            firstRelease.Set();
            Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, allocator.Frees);
        }
        finally
        {
            firstRelease.Set();
            secondRelease.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, allocator.Frees);
    }

    [Fact]
    public void ThrowingOperationStillReleasesItsRetention()
    {
        var allocator = new Allocator();
        var buffer = NativeDllBuffer.Allocate(1, allocator);
        Assert.Throws<InvalidOperationException>(() => buffer.WithPointer<int>(_ =>
        {
            buffer.Dispose();
            throw new InvalidOperationException();
        }));
        Assert.Equal(1, allocator.Frees);
    }

    [Fact]
    public void FailedAllocationAndNegativeSizeDoNotFreeInvalidPointers()
    {
        var allocator = new Allocator { Fail = true };
        Assert.Throws<OutOfMemoryException>(() => NativeDllBuffer.Allocate(8, allocator));
        Assert.Equal(0, allocator.Frees);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDllBuffer.Allocate(-1, allocator));
    }

    private sealed class Allocator : INativeDllBufferAllocator
    {
        internal int Frees;

        internal bool Fail;

        public nint Allocate(int length) => Fail ? 0 : Marshal.AllocHGlobal(length);

        public void Free(nint address)
        {
            Interlocked.Increment(ref Frees);
            Marshal.FreeHGlobal(address);
        }
    }
}
