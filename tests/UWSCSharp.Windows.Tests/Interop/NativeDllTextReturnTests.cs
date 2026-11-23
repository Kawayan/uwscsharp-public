using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllTextReturnTests
{
    private const uint Commit = 0x1000;
    private const uint Reserve = 0x2000;
    private const uint Release = 0x8000;
    private const uint NoAccess = 0x01;
    private const uint ReadWrite = 0x04;

    [Fact]
    public void WideTerminatorCanCrossAReadablePageBoundary()
    {
        nint process = Native.GetCurrentProcess();
        int pageSize = Environment.SystemPageSize;
        nint memory = Native.VirtualAllocEx(process, 0, (nuint)(pageSize * 2),
            Commit | Reserve, ReadWrite);
        Assert.NotEqual(nint.Zero, memory);
        try
        {
            nint start = memory + pageSize - 3;
            byte[] utf16 = [0xe5, 0x65, 0, 0];
            Marshal.Copy(utf16, 0, start, utf16.Length);

            Value value = NativeDllTextReturn.Read(NativeDllPrimitive.Wstring,
                start, CancellationToken.None);
            Assert.Equal("日", value.Text);
        }
        finally
        {
            Assert.True(Native.VirtualFreeEx(process, memory, 0, Release));
        }
    }

    [Fact]
    public void UnreadablePageStopsScanningButReadableLastByteCanTerminate()
    {
        nint process = Native.GetCurrentProcess();
        int pageSize = Environment.SystemPageSize;
        nint memory = Native.VirtualAllocEx(process, 0, (nuint)(pageSize * 2),
            Reserve, NoAccess);
        Assert.NotEqual(nint.Zero, memory);
        try
        {
            Assert.Equal(memory, Native.VirtualAllocEx(process, memory,
                (nuint)pageSize, Commit, ReadWrite));
            nint start = memory + pageSize - 2;
            Marshal.WriteByte(start, (byte)'A');
            Value value = NativeDllTextReturn.Read(NativeDllPrimitive.String,
                start, CancellationToken.None);
            Assert.Equal("A", value.Text);

            Marshal.WriteByte(start + 1, (byte)'B');
            Assert.Throws<InvalidOperationException>(() =>
                NativeDllTextReturn.Read(NativeDllPrimitive.String,
                    start, CancellationToken.None));
        }
        finally
        {
            Assert.True(Native.VirtualFreeEx(process, memory, 0, Release));
        }
    }

    [Fact]
    public void ArgumentPointerCannotScanPastItsOwnedCopy()
    {
        Assert.Throws<InvalidOperationException>(() => NativeDllTextReturn.Read(
            NativeDllPrimitive.Pchar, 101, CancellationToken.None,
            [[(byte)'A', (byte)'B']], [100]));
    }
}
