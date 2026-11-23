using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabClickBufferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RectangleIgnoresZeroResultAndRetainsSharedPrefix(bool partial)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        byte[] bytes = new byte[16];
        Buffer.BlockCopy(new[] { 10, 20, 21, 35 }, 0, bytes, 0, bytes.Length);
        owner.WriteRetainingBytes(bytes);
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, index, pointer) =>
        {
            if (message != 0x130a)
            {
                return null;
            }

            Assert.Equal(0, index);
            Assert.Equal(owner.Acquire().Address, pointer);
            if (partial)
            {
                Marshal.WriteInt32(pointer, 99);
            }

            return 0;
        });
        var reader = new NativeTabClickBuffer(owner);
        Native.Rect rectangle = reader.ReadRectangle(tab, 0, default);
        Assert.Equal((partial ? 99 : 10, 20, 21, 35),
            (rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void ZeroResultAndPartialWriteRetainPriorTextWithinCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "first", "second");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, index, pointer) =>
        {
            if (message != 0x133c)
            {
                return null;
            }

            nint text = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12);
            char[] value = (index == 0 ? "abcdef\0" : "XY").ToCharArray();
            Marshal.Copy(value, 0, text, value.Length);
            return 0;
        });
        var reader = new NativeTabClickBuffer(owner);
        Assert.Equal(["abcdef", "XYcdef"],
            reader.ReadNames(tab, default).Select(item => item.Name));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public void NonpositiveCountKeepsOriginalCheckedBoundary(int count)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "unused");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        using var hook = new ToolbarClickTextTests.Interceptor(tab,
            (message, _, _) => message == 0x1304 ? count : null);
        var reader = new NativeTabClickBuffer(owner);
        if (count == int.MinValue)
        {
            Assert.Throws<OverflowException>(() => reader.ReadNames(tab, default).ToArray());
        }
        else
        {
            Assert.Empty(reader.ReadNames(tab, default));
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("cancel")]
    public async Task CandidatesShareTextAddressAndReleaseBeforeRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = TabControlTests.Create(fixture, 10, 0, "first");
        nint second = TabControlTests.Create(fixture, 40, 0, "second");
        using var cancellation = new CancellationTokenSource();
        List<(nint Item, nint Text, int Mask, int Capacity)> transfers = [];
        nint? Observe(uint message, nint index, nint pointer)
        {
            if (message == 0x133c)
            {
                transfers.Add((pointer,
                    Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12),
                    Marshal.ReadInt32(pointer),
                    Marshal.ReadInt32(pointer, IntPtr.Size == 8 ? 24 : 16)));
                if (outcome == "cancel")
                {
                    cancellation.Cancel();
                }
            }

            return null;
        }

        using var firstHook = new ToolbarClickTextTests.Interceptor(first, Observe);
        using var secondHook = new ToolbarClickTextTests.Interceptor(second, Observe);
        Native.ShowWindow(fixture.Root, 0);
        int restores = 0;
        ToolbarClickTextTests.RestorePlatform? platform = null;
        platform = new ToolbarClickTextTests.RestorePlatform
        {
            Preparing = root =>
            {
                Assert.Equal(fixture.Root, root);
                Assert.False(platform!.Buffers.Released);
                Assert.False(platform.Buffers.Closed);
            },
            Restoring = () =>
            {
                restores++;
                Assert.True(platform!.Buffers.Released);
                Assert.True(platform.Buffers.Closed);
            },
        };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String(outcome == "missing" ? "missing" : "second"),
            Value.Numeric(8 | (outcome == "missing" ? 536870912 : 0))];
        if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(outcome == "success" ? 1 : 0),
                await session.InvokeAsync("CLKITEM", args, default));
            Assert.Equal(2, transfers.Count);
        }

        Assert.All(transfers, transfer =>
        {
            Assert.Equal(platform.Buffers.Address, transfer.Text);
            Assert.Equal(transfer.Text + 512, transfer.Item);
            Assert.Equal(1, transfer.Mask);
            Assert.Equal(256, transfer.Capacity);
        });
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task FirstMatchDoesNotReadLaterFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "first", "later");
        int laterReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, index, _) =>
        {
            if (message == 0x133c && index == 1)
            {
                laterReads++;
                return 0;
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first"), Value.Numeric(8)], default));
        Assert.Equal(0, laterReads);
    }

    [Fact]
    public async Task TabMissAndToolbarSuccessUseTheSameOwnerAddress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "tab");
        nint toolbar = ToolbarControlTests.Create(fixture, 40);
        ToolbarControlTests.Add(toolbar, "toolbar", 701);
        nint tabText = 0;
        nint toolbarText = 0;
        using var tabHook = new ToolbarClickTextTests.Interceptor(tab, (message, _, pointer) =>
        {
            if (message == 0x133c)
            {
                tabText = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12);
            }

            return null;
        });
        using var toolbarHook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x44b && pointer != 0)
            {
                toolbarText = pointer;
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("toolbar"), Value.Numeric(8 | 64)], default));
        Assert.NotEqual(0, tabText);
        Assert.Equal(tabText, toolbarText);
    }
}
