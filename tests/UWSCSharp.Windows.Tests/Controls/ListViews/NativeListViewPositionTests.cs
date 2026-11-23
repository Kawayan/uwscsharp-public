using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewPositionTests
{
    private sealed class Platform(nint root, List<string> events,
        CancellationTokenSource? cancelKey = null) : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint target) =>
            new(target, Buffers);

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (target == 77)
            {
                Assert.True(Buffers.Released);
                Assert.True(Buffers.Closed);
                events.Add("restore");
            }
            else
            {
                Assert.Equal(root, target);
                Assert.False(Buffers.Released);
                events.Add("activate");
            }
        }

        public override void LockUpdates(nint target) =>
            events.Add(target == 0 ? "unlock" : "lock");

        public override nint Send(nint target, uint message, nint parameter,
            nint pointer, bool timed, uint timeout, uint flags)
        {
            if (message is 0x100 or 0x101)
            {
                events.Add($"{(message == 0x100 ? "down" : "up")}:{parameter}");
                if (cancelKey is not null)
                {
                    nint result = base.Send(target, message, parameter, pointer, timed, timeout, flags);
                    if (message == 0x100)
                    {
                        cancelKey.Cancel();
                    }

                    return result;
                }

                return 1;
            }

            return base.Send(target, message, parameter, pointer, timed, timeout, flags);
        }
    }

    [Fact]
    public void ZeroResultAndPartialPointWriteKeepKnownYWithoutClearingBase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        int calls = 0;
        List<nint> pointers = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, _, pointer) =>
        {
            if (message != 0x1010)
            {
                return null;
            }

            pointers.Add(pointer);
            Marshal.WriteInt32(pointer, calls == 0 ? 10 : 30);
            if (calls++ == 0)
            {
                Marshal.WriteInt32(pointer, 4, 20);
            }

            return 0;
        });
        var buffer = new NativeListViewClickBuffer(owner);
        Assert.Equal(new Native.Point(10, 20), buffer.ReadFirstPosition(view, default));
        Assert.Equal(new Native.Point(30, 20), buffer.ReadSelectedPosition(view, 2, default));
        Assert.All(pointers, pointer => Assert.Equal(owner.Acquire().Address, pointer));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task YComparisonOrdersNeighborKeysBetweenSharedPointReads(bool sameY, bool back)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        List<string> events = [];
        List<nint> pointers = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, pointer) =>
        {
            if (message == 0x1010)
            {
                events.Add($"position:{row}");
                pointers.Add(pointer);
                Marshal.WriteInt32(pointer, (int)row + 1);
                Marshal.WriteInt32(pointer, 4, row == 0 || sameY ? 10 : 20);
                return 0;
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        var platform = new Platform(fixture.Root, events);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), Value.Numeric(32 | (back ? 512 : 0)), Value.Numeric(0)], default));
        List<string> expected = ["position:0", "position:2"];
        if (!back)
        {
            expected.Add("activate");
        }

        int first = sameY ? 40 : 38;
        int second = sameY ? 38 : 40;
        expected.AddRange(["lock", $"down:{first}", $"up:{first}",
            $"down:{second}", $"up:{second}", "unlock", "position:2", "restore"]);
        Assert.Equal(expected, events);
        Assert.Equal(3, pointers.Count);
        Assert.All(pointers, pointer => Assert.Equal(platform.Buffers.Address, pointer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionOrNeighborCancellationReleasesResourcesAndKeepsNativePartialState(bool duringKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        using var cancellation = new CancellationTokenSource();
        List<string> events = [];
        int calls = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, pointer) =>
        {
            if (message == 0x1010)
            {
                calls++;
                events.Add($"position:{row}");
                Marshal.WriteInt32(pointer, 0);
                Marshal.WriteInt32(pointer, 4, row == 0 ? 10 : 20);
                if (!duringKey && calls == 2)
                {
                    cancellation.Cancel();
                }

                return 1;
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        var platform = new Platform(fixture.Root, events, duringKey ? cancellation : null);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("same"),
                Value.Numeric(32 | 512), Value.Numeric(0)], cancellation.Token));
        string[] expected = duringKey
            ? ["position:0", "position:2", "lock", "down:38", "up:38", "unlock", "restore"]
            : ["position:0", "position:2", "restore"];
        Assert.Equal(expected, events);
        Assert.Equal((nint)3, Native.SendMessageW(view, 0x102c, duringKey ? 1 : 2, 3));
        Assert.Equal((nint)0, Native.SendMessageW(view, 0x102c, duringKey ? 2 : 1, 3));
        Assert.Null(fixture.CallbackFailure);
    }
}
