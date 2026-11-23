using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeControlTextBufferTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(nint process, nint address,
        nuint size, uint protection, out uint previous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address,
        [Out] byte[] bytes, nuint size, out nuint read);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void LocalBufferCanBeDisposedRepeatedlyAndRejectsFreedAccess(int kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        using var buffer = new NativeControlTextBuffer(fixture.Root, (ControlTextKind)kind);
        buffer.Prepare();
        Assert.Equal("", buffer.Read());
        Assert.NotEqual(0, buffer.TextAddress);
        buffer.Dispose();
        buffer.Dispose();
        AssertDisposed(buffer);
    }

    [Fact]
    public async Task RemoteBuffersReleaseVirtualMemoryBeforeRejectingFreedAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executable = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        Assert.True(child.Start());
        try
        {
            string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(line);
            nint root = (nint)long.Parse(line.Split(',')[0]);
            foreach (ControlTextKind kind in Enum.GetValues<ControlTextKind>())
            {
                using var buffer = new NativeControlTextBuffer(root, kind);
                buffer.Prepare();
                nint address = buffer.TextAddress;
                byte[] data = new byte[1];
                Assert.True(ReadProcessMemory(child.Handle, address, data, 1, out nuint read));
                Assert.Equal((nuint)1, read);
                buffer.WriteTextBytesRetaining([7, 9, 11, 13]);
                Assert.True(VirtualProtectEx(child.Handle, address, 16,
                    1, out uint previous));
                try
                {
                    Assert.False(ReadProcessMemory(child.Handle, address, new byte[4],
                        4, out nuint copied));
                    Assert.Equal((nuint)0, copied);
                    byte[] destination = [1, 2, 3, 4];
                    buffer.ReadTextBytesRetaining(destination);
                    Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination);
                    buffer.WriteTextBytesRetaining([21, 22, 23, 24]);
                }
                finally
                {
                    Assert.True(VirtualProtectEx(child.Handle, address, 16,
                        previous, out _));
                }

                byte[] actual = new byte[4];
                buffer.ReadTextBytesRetaining(actual);
                Assert.Equal(new byte[] { 7, 9, 11, 13 }, actual);
                if (kind == ControlTextKind.Toolbar)
                {
                    VerifyReadAcrossInaccessiblePage(child.Handle, buffer);
                }

                buffer.Dispose();
                buffer.Dispose();
                Assert.False(ReadProcessMemory(child.Handle, address, data, 1, out _));
                AssertDisposed(buffer);
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static void VerifyReadAcrossInaccessiblePage(nint process,
        NativeControlTextBuffer buffer)
    {
        int page = Environment.SystemPageSize;
        byte[] source = [.. Enumerable.Repeat((byte)7, page * 2)];
        buffer.WriteTextBytesRetaining(source);
        nint secondPage = buffer.TextAddress + page;
        Assert.True(VirtualProtectEx(process, secondPage, (nuint)page,
            1, out uint previous));
        try
        {
            byte[] expected = [.. Enumerable.Repeat((byte)0x55, source.Length)];
            Assert.False(ReadProcessMemory(process, buffer.TextAddress, expected,
                (nuint)expected.Length, out nuint copied));
            // この環境では範囲全体が失敗し、先頭pageの転送も行われません。
            Assert.Equal((nuint)0, copied);
            Assert.All(expected, value => Assert.Equal((byte)0x55, value));
            byte[] actual = [.. Enumerable.Repeat((byte)0x55, source.Length)];
            buffer.ReadTextBytesRetaining(actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Assert.True(VirtualProtectEx(process, secondPage, (nuint)page,
                previous, out _));
        }
    }

    private static void AssertDisposed(NativeControlTextBuffer buffer)
    {
        Assert.Throws<ObjectDisposedException>(() => buffer.Prepare());
        Assert.Throws<ObjectDisposedException>(() => buffer.Read());
        Assert.Throws<ObjectDisposedException>(() => buffer.ReadItemBytes());
        Assert.Throws<ObjectDisposedException>(() => buffer.ReadTextBytesRetaining(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => buffer.WriteTextBytesRetaining([1]));
        Assert.Throws<ObjectDisposedException>(() => buffer.ReadStringAt(0, default));
        Assert.Throws<ObjectDisposedException>(() => { _ = buffer.ItemAddress; });
        Assert.Throws<ObjectDisposedException>(() => { _ = buffer.TextAddress; });
    }
}
