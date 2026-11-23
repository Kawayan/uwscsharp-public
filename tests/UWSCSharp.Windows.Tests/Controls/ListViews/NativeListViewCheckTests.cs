using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewCheckTests
{
    private sealed class Platform(bool control, CancellationTokenSource cancel,
        string? failure) : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal List<int> Keys { get; } = [];
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
                Assert.True(Buffers.Released);
                Assert.True(Buffers.Closed);
                Restores++;
            }
        }

        public override short KeyState(int key) => control ? short.MinValue : (short)0;

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (message is 0x100 or 0x101)
            {
                Keys.Add(message == 0x100 ? (int)parameter : -(int)parameter);
                if (message == 0x100)
                {
                    if (failure == "key")
                    {
                        cancel.Cancel();
                    }

                    if (failure == "throw_key")
                    {
                        throw new InvalidOperationException("key callback");
                    }
                }

                return 1;
            }

            return base.Send(target, message, parameter, data, timed, timeout, flags);
        }
    }

    [Theory]
    [InlineData(0, false, false, 0)]
    [InlineData(1, false, false, 0)]
    [InlineData(2, false, false, 1)]
    [InlineData(3, false, false, 0)]
    [InlineData(2, true, false, 1)]
    [InlineData(2, true, true, 1)]
    public async Task QueryUsesColumnFallbackSelectionAndTwoPointsWithoutAction(
        int image, bool fallback, bool control, int expected)
    {
        await Run(image, fallback, control, null, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNameStillAppliesControlPolicy(bool control)
    {
        await Run(2, false, control, "missing", -1);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("state")]
    [InlineData("point")]
    [InlineData("key")]
    [InlineData("throw_key")]
    [InlineData("reject")]
    public async Task FailureKeepsCompletedStateAndReleasesBeforeRestore(string failure)
    {
        await Run(2, false, false, failure, failure == "reject" ? 1 : -1);
    }

    private static async Task Run(int image, bool fallback, bool control,
        string? failure, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        if (fallback)
        {
            for (int row = 0; row < 5; row++)
            {
                ListViewControlTests.SetText(view, row, "");
            }
        }

        ListViewControlTests.SetText(view, 0, "target", fallback ? 1 : 0);
        Native.SendMessageW(view, 0x1036, 4, 4);
        ListViewControlTests.SetStateImage(view, 0, image);
        ListViewControlTests.Select(view, 3);
        if (failure == "missing")
        {
            // 未一致後のUIA探索は別の保留範囲なので、native結果の返却条件を満たします。
            for (int index = 0; index < 20; index++)
            {
                fixture.Child("BUTTON", $"other{index}", 0, 100);
            }
        }

        using var cancellation = new CancellationTokenSource();
        var platform = new Platform(control, cancellation, failure);
        int points = 0;
        int actionKeys = 0;
        List<int> columns = [];
        List<nint> bases = [];
        fixture.Notify = (_, pointer) =>
            failure == "reject" && Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code == -100 ? 1 : 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, parameter, pointer) =>
        {
            if (message == 0x1073)
            {
                columns.Add(Marshal.ReadInt32(pointer, 8));
                bases.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 20));
                if (failure == "name")
                {
                    cancellation.Cancel();
                }
            }

            if (message == 0x102b && failure == "state")
            {
                cancellation.Cancel();
            }

            if (message == 0x1010)
            {
                points++;
                bases.Add(pointer);
                if (failure == "point")
                {
                    cancellation.Cancel();
                }
            }

            if (message == 0x100 && parameter is 13 or 32)
            {
                actionKeys++;
            }

            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String(failure == "missing" ? "missing" : "target")];
        if (failure is "name" or "state" or "point" or "key")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CHKBTN", args, cancellation.Token));
        }
        else if (failure == "throw_key")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CHKBTN", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN", args, default));
        }

        Assert.Equal(1, platform.Restores);
        Assert.All(bases, address => Assert.Equal(platform.Buffers.Address, address));
        Assert.Equal(0, actionKeys);
        Assert.Equal(failure == "name" || failure == "missing" ? 0
            : failure == "point" ? 1 : failure == "state" ? 0 : 2, points);
        if (failure is null)
        {
            Assert.Equal(fallback ? [0, 1] : new[] { 0 }, columns.Distinct());
            Assert.Equal([40, -40, 38, -38], platform.Keys);
        }

        if (failure is "key" or "throw_key")
        {
            Assert.Equal([40, -40], platform.Keys);
        }

        Assert.Equal((nint)(failure is "name" or "reject" or "missing" ? 0 : 3),
            Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Equal((nint)(control || failure is "name" or "state" or "reject" ? 2 : 0),
            Native.SendMessageW(view, 0x102c, 3, 3));
        Assert.Equal((nint)(image << 12), Native.SendMessageW(view, 0x102c, 0, 0xf000));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidatesShareRootBufferAndQueryLastOccurrence(bool hidden)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = ListViewControlTests.Create(fixture, 10);
        nint second = ListViewControlTests.Create(fixture, 60);
        Native.SendMessageW(second, 0x1036, 4, 4);
        ListViewControlTests.SetStateImage(second, 2, 2);
        ListViewControlTests.Select(first, 0);
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform(false, cancellation, null);
        List<nint> bases = [];
        nint? Observe(uint message, nint parameter, nint pointer)
        {
            if (message == 0x1073)
            {
                bases.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 20));
            }

            return null;
        }

        using var firstHook = new ToolbarClickTextTests.Interceptor(first, Observe);
        using var secondHook = new ToolbarClickTextTests.Interceptor(second, Observe);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("same"), Value.Numeric(3)], default));
        Assert.Equal((nint)0, Native.SendMessageW(first, 0x102c, 0, 3));
        Assert.Equal((nint)3, Native.SendMessageW(second, 0x102c, 2, 3));
        Assert.Equal(10, bases.Count);
        Assert.All(bases, address => Assert.Equal(platform.Buffers.Address, address));
        Assert.Equal(1, platform.Restores);
    }
}
