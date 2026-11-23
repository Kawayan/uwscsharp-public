using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewClickBufferTests
{
    private static nint Text(nint item) =>
        Marshal.ReadIntPtr(item, IntPtr.Size == 8 ? 24 : 20);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public void NonpositiveRowCountKeepsCheckedBoundary(int count)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        using var hook = new ToolbarClickTextTests.Interceptor(view,
            (message, _, _) => message == 0x1004 ? count : null);
        var buffer = new NativeListViewClickBuffer(owner);
        if (count == int.MinValue)
        {
            Assert.Throws<OverflowException>(() => buffer.ReadNames(view, default));
        }
        else
        {
            Assert.Empty(buffer.ReadNames(view, default));
        }
    }

    [Fact]
    public void ZeroResultAndPartialWriteRetainPreviousRowText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, item) =>
        {
            if (message != 0x1073)
            {
                return null;
            }

            char[] value = (row == 0 ? "abcdef\0" : row == 1 ? "XY" : "\0").ToCharArray();
            Marshal.Copy(value, 0, Text(item), value.Length);
            return 0;
        });
        var buffer = new NativeListViewClickBuffer(owner);
        Assert.Equal(["abcdef", "XYcdef", "", "", ""], buffer.ReadNames(view, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ColumnFallbackStopsAtFirstNonemptyColumn(bool emptyPrimary)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        List<int> columns = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, item) =>
        {
            if (message != 0x1073)
            {
                return null;
            }

            int column = Marshal.ReadInt32(item, 8);
            columns.Add(column);
            string text = column == 1 && row == 2 ? "target"
                : !emptyPrimary && column == 0 && row == 0 ? "primary" : "";
            char[] value = (text + "\0").ToCharArray();
            Marshal.Copy(value, 0, Text(item), value.Length);
            return text.Length;
        });
        Assert.Equal((emptyPrimary ? 1 : 0, emptyPrimary, emptyPrimary),
            NativeListViewControl.Click(view, "target", 1, false, default,
                platform: new OwnedListSelectionPlatform()));
        Assert.Equal(emptyPrimary ? 10 : 5, columns.Count);
        Assert.All(columns, column => Assert.InRange(column, 0, emptyPrimary ? 1 : 0));
        Assert.Equal(emptyPrimary ? 3 : 0, Native.SendMessageW(view, 0x102c, 2, 3));
    }

    [Fact]
    public void EmptyViewTextProbesEightColumnsAndClearsSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.Select(view, 0);
        List<int> columns = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, _, item) =>
        {
            if (message == 0x1073)
            {
                columns.Add(Marshal.ReadInt32(item, 8));
                Marshal.WriteInt16(Text(item), 0);
                return 0;
            }

            return null;
        });
        Assert.Equal((0, false, false), NativeListViewControl.Click(view, "missing", 1, false, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(40, columns.Count);
        Assert.Equal(Enumerable.Range(0, 8), columns.Distinct());
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 0, 3));
    }

    [Fact]
    public void SingleSelectionInterleavesReadAndStateWriteOnSameItemAddress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        List<string> messages = [];
        List<nint> pointers = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, item) =>
        {
            if (message is 0x1073 or 0x102b)
            {
                messages.Add($"{(message == 0x1073 ? "R" : "W")}{row}");
                pointers.Add(item);
            }

            return null;
        });
        Assert.Equal((2, true, true), NativeListViewControl.Click(view, "same", 1, false, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(["R0", "W0", "R1", "W1", "R2", "W2", "R3", "W3", "R4", "W4"], messages);
        Assert.All(pointers, pointer => Assert.Equal(pointers[0], pointer));
    }

    [Fact]
    public void CancellationDuringLaterReadKeepsEarlierSelectionChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.Select(view, 0);
        using var cancellation = new CancellationTokenSource();
        int updates = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, _) =>
        {
            if (message == 0x1073 && row == 2)
            {
                cancellation.Cancel();
            }

            if (message == 0x102b)
            {
                updates++;
            }

            return null;
        });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeListViewControl.Click(view, "same", 1, false, cancellation.Token,
                platform: new OwnedListSelectionPlatform()));
        Assert.Equal(2, updates);
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 0, 3));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("cancel")]
    public async Task CandidatesShareAddressAndReleaseBeforeRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = ListViewControlTests.Create(fixture, 10);
        nint second = ListViewControlTests.Create(fixture, 60);
        using var cancellation = new CancellationTokenSource();
        List<(nint Text, int Mask, int Capacity)> transfers = [];
        nint? Observe(uint message, nint row, nint item)
        {
            if (message == 0x1073)
            {
                transfers.Add((Text(item), Marshal.ReadInt32(item),
                    Marshal.ReadInt32(item, IntPtr.Size == 8 ? 32 : 24)));
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
        Value[] args = [id, Value.String(outcome == "missing" ? "missing" : "same"),
            Value.Numeric(32 | 512), Value.Numeric(1), Value.Numeric(3)];
        if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(outcome == "success" ? 1 : 0),
                await session.InvokeAsync("CLKITEM", args, default));
            Assert.Equal(10, transfers.Count);
        }

        Assert.All(transfers, transfer =>
        {
            Assert.Equal(platform.Buffers.Address, transfer.Text);
            Assert.Equal(1, transfer.Mask);
            Assert.Equal(256, transfer.Capacity);
        });
        Assert.Equal(1, restores);
    }
}
