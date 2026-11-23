using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeCheckTests
{
    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal int Restores;

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            new(root, Buffers);

        public override bool IsVisible(nint root) => false;

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (root == 77)
            {
                if (Buffers.Address != 0)
                {
                    Assert.True(Buffers.Released);
                    Assert.True(Buffers.Closed);
                }

                Restores++;
            }
        }

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags) =>
            message is 0x100 or 0x101 ? 1
                : base.Send(target, message, parameter, data, timed, timeout, flags);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task QuerySelectsPathAndReadsOneSharedStateWithoutSpace(int image, bool hidden)
    {
        await Run(image, hidden, null);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("veto")]
    [InlineData("select_cancel")]
    [InlineData("state_cancel")]
    public async Task GetterFailureVetoAndCancellationPreserveCompletedEffects(string failure)
    {
        await Run(2, false, failure);
    }

    private static async Task Run(int image, bool hidden, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint parent = TreeControlTests.Add(tree, 0, "parent");
        nint item = TreeControlTests.Add(tree, parent, "target");
        nint later = TreeControlTests.Add(tree, item, "later");
        ViewCheckTests.SetState(tree, item, false, image);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform();
        List<string> events = [];
        int states = 0;
        int laterReads = 0;
        int legacyReads = 0;
        int keys = 0;
        fixture.Notify = (_, pointer) =>
        {
            int code = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code;
            if (code == -450)
            {
                events.Add("select");
                if (failure == "select_cancel")
                {
                    cancellation.Cancel();
                }

                return failure == "veto" ? 1 : 0;
            }

            return 0;
        };
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x113e)
            {
                Assert.Equal(platform.Buffers.Address + 512, pointer);
                nint current = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4);
                if (Marshal.ReadInt32(pointer) == 0x41)
                {
                    laterReads += current == later ? 1 : 0;
                    Assert.Equal(platform.Buffers.Address,
                        Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16));
                }
                else
                {
                    states++;
                    events.Add("state");
                    Assert.Equal(item, current);
                    Assert.Equal(8, Marshal.ReadInt32(pointer));
                    Assert.Equal(0xf000, Marshal.ReadInt32(pointer, IntPtr.Size == 8 ? 20 : 12));
                    if (failure == "state_cancel")
                    {
                        cancellation.Cancel();
                    }

                    if (failure == "zero")
                    {
                        Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 16 : 8, image << 12);
                        return 0;
                    }
                }
            }

            legacyReads += message == 0x1127 ? 1 : 0;
            keys += message == 0x100 ? 1 : 0;
            return null;
        });
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("\\parent\\target")];
        if (failure is "select_cancel" or "state_cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CHKBTN", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(image == 2 ? 1 : 0),
                await session.InvokeAsync("CHKBTN", args, default));
        }

        Assert.Equal(0, keys);
        Assert.Equal(0, legacyReads);
        Assert.Equal(0, laterReads);
        Assert.Equal(failure == "select_cancel" ? 0 : 1, states);
        Assert.Equal(1, platform.Restores);
        if (states != 0)
        {
            Assert.True(events.IndexOf("select") < events.IndexOf("state"));
        }

        Assert.Equal(failure == "veto" ? 0 : failure == "select_cancel" ? parent : item,
            Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TreeMissAndViewMatchBorrowTheSameRootAddress(bool hidden)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "tree");
        nint view = ListViewControlTests.Create(fixture, 60);
        ListViewControlTests.SetText(view, 0, "target");
        Native.SendMessageW(view, 0x1036, 4, 4);
        ListViewControlTests.SetStateImage(view, 0, 2);
        var platform = new Platform();
        List<nint> addresses = [];
        using var treeHook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e)
            {
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16));
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
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target")], default));
        Assert.Equal(6, addresses.Count);
        Assert.All(addresses, address => Assert.Equal(platform.Buffers.Address, address));
        Assert.Equal(1, platform.Restores);
    }

    [Fact]
    public async Task DefaultNativeQueryDoesNotReadToolbarAndRestoresAfterTreeMiss()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        ToolbarOperationTests.AddNativeCandidates(fixture);
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "tree");
        nint toolbar = ToolbarControlTests.Create(fixture, 60);
        ToolbarControlTests.Add(toolbar, "target", 701);
        int reads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, _) =>
        {
            if (message is 0x417 or 0x44b or 0x418)
            {
                reads++;
            }

            return null;
        });
        var platform = new Platform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target")], default));
        Assert.Equal(0, reads);
        Assert.NotEqual(0, platform.Buffers.Address);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ButtonQueryRestoresWithoutAllocatingSharedMemory(bool hidden)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "target", 3, 10);
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        var platform = new Platform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target")], default));
        Assert.Equal(0, platform.Buffers.Address);
        Assert.False(platform.Buffers.Released);
        Assert.False(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
    }
}
