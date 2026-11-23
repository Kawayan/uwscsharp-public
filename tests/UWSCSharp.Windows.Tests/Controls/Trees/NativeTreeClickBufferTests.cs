using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeClickBufferTests
{
    private static nint Create(ButtonControlTests.Fixture fixture, int top)
    {
        SliderControlTests.InitializeControls(2);
        return fixture.Child("SysTreeView32", "", 0, top);
    }

    [Fact]
    public void NodesStartWithZeroTextEvenAfterPreviousPartialFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tree = Create(fixture, 10);
        nint first = TreeControlTests.Add(tree, 0, "first");
        nint second = TreeControlTests.Add(tree, 0, "second");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message != 0x113e)
            {
                return null;
            }

            nint item = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4);
            nint text = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16);
            char[] value = (item == first ? "abcdef\0" : "XY").ToCharArray();
            Marshal.Copy(value, 0, text, value.Length);
            return 0;
        });
        var buffer = new NativeTreeClickBuffer(owner);
        Assert.Equal(("abcdef", 0), buffer.ReadItem(tree, first, default));
        Assert.Equal(("XY", 0), buffer.ReadItem(tree, second, default));
    }

    [Fact]
    public void ReportedZeroChildrenDoesNotProbeActualChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tree = Create(fixture, 10);
        nint root = TreeControlTests.Add(tree, 0, "root");
        TreeControlTests.Add(tree, root, "child");
        int childProbes = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x110a && parameter == 4)
            {
                childProbes++;
            }

            if (message == 0x113e)
            {
                nint text = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16);
                char[] value = "root\0".ToCharArray();
                Marshal.Copy(value, 0, text, value.Length);
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 44 : 32, 0);
                return 1;
            }

            return null;
        });
        Assert.Equal((false, false), NativeTreeControl.Click(tree, "child", true, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(0, childProbes);
    }

    [Fact]
    public void FirstMatchDoesNotReadFailingChildOrSibling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tree = Create(fixture, 10);
        nint root = TreeControlTests.Add(tree, 0, "first");
        TreeControlTests.Add(tree, root, "child");
        TreeControlTests.Add(tree, 0, "sibling");
        int laterReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e
                && Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4) != root)
            {
                laterReads++;
                return 0;
            }

            return null;
        });
        Assert.Equal((true, true), NativeTreeControl.Click(tree, "first", true, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.Equal(0, laterReads);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("cancel")]
    public async Task CandidatesShareTransferAndReleaseBeforeRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = Create(fixture, 10);
        nint second = Create(fixture, 40);
        nint firstItem = TreeControlTests.Add(first, 0, "first");
        nint secondItem = TreeControlTests.Add(second, 0, "second");
        using var cancellation = new CancellationTokenSource();
        List<(nint Text, nint Handle, int Mask, int Capacity)> transfers = [];
        nint? Observe(uint message, nint parameter, nint pointer)
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                transfers.Add((
                    Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16),
                    Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4),
                    Marshal.ReadInt32(pointer),
                    Marshal.ReadInt32(pointer, IntPtr.Size == 8 ? 32 : 20)));
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
        Value[] args = [id, Value.String(outcome == "missing" ? "missing" : "second"),
            Value.Numeric(16 | (outcome == "missing" ? 536870912 : 0))];
        if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(outcome == "success" ? 1 : 0),
                await session.InvokeAsync("CLKITEM", args, default));
            Assert.Equal([firstItem, secondItem], transfers.Select(item => item.Handle));
        }

        Assert.All(transfers, transfer =>
        {
            Assert.Equal(platform.Buffers.Address, transfer.Text);
            Assert.Equal(0x41, transfer.Mask);
            Assert.Equal(256, transfer.Capacity);
        });
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task TabTreeViewAndToolbarCandidatesBorrowSameAddress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "tab");
        nint tree = Create(fixture, 40);
        TreeControlTests.Add(tree, 0, "tree");
        nint view = ListViewControlTests.Create(fixture, 70);
        nint toolbar = ToolbarControlTests.Create(fixture, 100);
        ToolbarControlTests.Add(toolbar, "toolbar", 701);
        List<nint> addresses = [];
        using var tabHook = new ToolbarClickTextTests.Interceptor(tab, (message, _, pointer) =>
        {
            if (message == 0x133c)
            {
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12));
            }

            return null;
        });
        using var treeHook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e)
            {
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16));
            }

            return null;
        });
        using var toolbarHook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x44b && pointer != 0)
            {
                addresses.Add(pointer);
            }

            return null;
        });
        using var viewHook = new ToolbarClickTextTests.Interceptor(view, (message, _, pointer) =>
        {
            if (message == 0x1073)
            {
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 20));
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("toolbar"), Value.Numeric(8 | 16 | 32 | 64)], default));
        Assert.Equal(8, addresses.Count);
        Assert.All(addresses, address => Assert.Equal(addresses[0], address));
    }
}
