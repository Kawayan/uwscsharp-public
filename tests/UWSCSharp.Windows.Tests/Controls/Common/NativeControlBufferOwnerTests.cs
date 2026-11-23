using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeControlBufferOwnerTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size,
        uint protection, out uint previous);

    [Fact]
    public void InaccessibleRealBufferRetainsDestinationAndDoesNotApplyWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        using var owner = new NativeControlBufferOwner(fixture.Root);
        (nint process, nint address) = owner.Acquire();
        owner.WriteRetainingBytes([7, 9, 11, 13]);
        Assert.True(VirtualProtect(address, NativeControlBufferOwner.Capacity,
            1, out uint previous));
        try
        {
            Assert.False(ReadProcessMemory(process, address, new byte[4], 4, out nuint copied));
            Assert.Equal((nuint)0, copied);
            byte[] destination = [1, 2, 3, 4];
            owner.ReadRetainingBytes(destination);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination);
            owner.WriteRetainingBytes([21, 22, 23, 24]);
        }
        finally
        {
            Assert.True(VirtualProtect(address, NativeControlBufferOwner.Capacity,
                previous, out _));
        }

        byte[] actual = new byte[4];
        owner.ReadRetainingBytes(actual);
        Assert.Equal(new byte[] { 7, 9, 11, 13 }, actual);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void FailedReadRetainsKnownBytesAndRejectsOversizedTransfer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var platform = new Platform();
        using var owner = new NativeControlBufferOwner(77, platform);
        byte[] bytes = [1, 2, 3, 4];
        owner.ReadRetainingBytes(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        owner.WriteRetainingBytes(bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            owner.ReadRetainingBytes(new byte[4097]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            owner.WriteRetainingBytes(new byte[4097]));
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.ReadRetainingBytes(bytes));
        Assert.Throws<ObjectDisposedException>(() => owner.WriteRetainingBytes(bytes));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address,
        [Out] byte[] bytes, nuint size, out nuint read);

    [Fact]
    public void AcquisitionIsLazyAndReusesRootProcessUntilOwnerExit()
    {
        var platform = new Platform();
        using var owner = new NativeControlBufferOwner(77, platform);
        Assert.Empty(platform.Events);
        Assert.Equal(((nint)11, (nint)22), owner.Acquire());
        Assert.Equal(owner.Acquire(), owner.Acquire());
        Assert.Equal(new[] { "open:77", "allocate:11" }, platform.Events);
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(new[] { "open:77", "allocate:11", "free:11:22", "close:11" }, platform.Events);
        Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
    }

    [Fact]
    public void UnusedOwnerDoesNotAcquireOrReleaseResources()
    {
        var platform = new Platform();
        using var owner = new NativeControlBufferOwner(77, platform);
        owner.Dispose();
        Assert.Empty(platform.Events);
        Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
    }

    [Fact]
    public void OpenFailureCanBeRetriedWithoutClosingAnInvalidHandle()
    {
        var platform = new Platform { OpenResult = 0 };
        using var owner = new NativeControlBufferOwner(77, platform);
        Assert.Throws<Win32Exception>(() => owner.Acquire());
        platform.OpenResult = 11;
        owner.Acquire();
        Assert.Equal(new[] { "open:77", "open:77", "allocate:11" }, platform.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocationFailureClosesHandleAndAllowsNextAttempt(bool managedFailure)
    {
        var platform = new Platform { AllocationResult = 0, AllocationFailure = managedFailure };
        using var owner = new NativeControlBufferOwner(77, platform);
        if (managedFailure)
        {
            Assert.Throws<InvalidOperationException>(() => owner.Acquire());
        }
        else
        {
            Assert.Throws<Win32Exception>(() => owner.Acquire());
        }

        Assert.Equal(new[] { "open:77", "allocate:11", "close:11" }, platform.Events);
        platform.AllocationResult = 22;
        platform.AllocationFailure = false;
        Assert.Equal(((nint)11, (nint)22), owner.Acquire());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseFailureStillClosesHandleAndDoesNotRepeat(bool closeFails)
    {
        var platform = new Platform { FreeFailure = !closeFails, CloseFailure = closeFails };
        using var owner = new NativeControlBufferOwner(77, platform);
        owner.Acquire();
        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        owner.Dispose();
        Assert.Equal(new[] { "open:77", "allocate:11", "free:11:22", "close:11" }, platform.Events);
        Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
    }

    [Fact]
    public void RealAllocationIncludesAll4096BytesAndIsReleasedOnExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        using var owner = new NativeControlBufferOwner(fixture.Root);
        (nint process, nint address) = owner.Acquire();
        byte[] bytes = new byte[NativeControlBufferOwner.Capacity];
        Assert.True(ReadProcessMemory(process, address, bytes, (nuint)bytes.Length, out nuint read));
        Assert.Equal((nuint)4096, read);
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
        Assert.Equal(Environment.Is64BitProcess, owner.Is64Bit);
        owner.WriteRetainingBytes([7, 9]);
        byte[] prefix = new byte[2];
        owner.ReadRetainingBytes(prefix);
        Assert.Equal(new byte[] { 7, 9 }, prefix);
        Marshal.WriteByte(address + 4095, 123);
        Assert.Equal((byte)123, Marshal.ReadByte(owner.Acquire().Address + 4095));
        owner.Dispose();
        Assert.False(ReadProcessMemory(-1, address, bytes, 1, out _));
    }

    private sealed class Platform : INativeControlBufferPlatform
    {
        internal List<string> Events { get; } = [];
        internal nint OpenResult { get; set; } = 11;
        internal nint AllocationResult { get; set; } = 22;
        internal bool AllocationFailure
        {
            get; set;
        }
        internal bool FreeFailure
        {
            get; init;
        }
        internal bool CloseFailure
        {
            get; init;
        }

        public nint Open(nint root)
        {
            Events.Add($"open:{root}");
            return OpenResult;
        }

        public nint Allocate(nint process)
        {
            Events.Add($"allocate:{process}");
            if (AllocationFailure)
            {
                throw new InvalidOperationException("確保例外");
            }

            return AllocationResult;
        }

        public void Free(nint process, nint address)
        {
            Events.Add($"free:{process}:{address}");
            if (FreeFailure)
            {
                throw new InvalidOperationException("領域解放例外");
            }
        }

        public void Close(nint process)
        {
            Events.Add($"close:{process}");
            if (CloseFailure)
            {
                throw new InvalidOperationException("handle解放例外");
            }
        }
    }
}
