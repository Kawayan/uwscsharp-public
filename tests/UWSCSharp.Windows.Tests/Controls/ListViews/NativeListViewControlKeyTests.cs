using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewControlKeyTests
{
    private sealed class Platform(short[] states, CancellationTokenSource cancel,
        string? failure) : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal List<(int Row, int State)> Writes { get; } = [];
        internal int Restores
        {
            get;
            private set;
        }

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            new(root, Buffers);

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.Equal(77, root);
            Assert.True(Buffers.Released);
            Assert.True(Buffers.Closed);
            Restores++;
        }

        public override short KeyState(int key)
        {
            Assert.Equal(17, key);
            nint pointer = Buffers.Address + 512;
            Assert.Equal(8, Marshal.ReadInt32(pointer));
            Assert.Equal(3, Marshal.ReadInt32(pointer, 16));
            int row = Marshal.ReadInt32(pointer, 4);
            Writes.Add((row, Marshal.ReadInt32(pointer, 12)));
            if (row == 1)
            {
                if (failure == "cancel")
                {
                    cancel.Cancel();
                }

                if (failure == "throw")
                {
                    throw new InvalidOperationException("key state callback");
                }
            }

            return states[Math.Min(row, states.Length - 1)];
        }

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            // CTRLの実OS状態を変更せず、隣接キー通知だけを記録用に置換します。
            return message is 0x100 or 0x101 ? 1
                : base.Send(target, message, parameter, data, timed, timeout, flags);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(128, true)]
    [InlineData(-32768, true)]
    [InlineData(-32767, true)]
    public async Task SignedShortMaskSuppressesOnlyNonmatchingStateMessages(int state, bool preserve)
    {
        await Run([(short)state], true, preserve, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNameUsesTheSamePerRowPolicy(bool preserve)
    {
        await Run([preserve ? short.MinValue : (short)0], false, preserve, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyStateIsQueriedAgainAfterEachWrite(bool heldAtEnd)
    {
        short held = short.MinValue;
        short[] states = heldAtEnd ? [0, 0, 0, held, held] : [held, held, held, 0, 0];
        await Run(states, true, heldAtEnd, null);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("throw")]
    public async Task KeyQueryFailureKeepsPriorSelectionAndReleases(string failure)
    {
        await Run([short.MinValue], true, true, failure);
    }

    private static async Task Run(short[] states, bool found,
        bool preserveLast, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "item");
        ListViewControlTests.Select(view, 3);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform(states, cancellation, failure);
        List<int> sentRows = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, row, pointer) =>
        {
            if (message == 0x102b)
            {
                sentRows.Add((int)row);
                Assert.Equal(platform.Buffers.Address + 512, pointer);
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String(found ? "item" : "missing"), Value.Numeric(32 | 512), Value.Numeric(0)];
        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else if (failure == "throw")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(found ? 1 : 0), await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(failure is null ? 5 : 2, platform.Writes.Count);
        Assert.Equal(1, platform.Restores);
        Assert.Equal((nint)(found ? 3 : 0), Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Equal((nint)(preserveLast ? 2 : 0), Native.SendMessageW(view, 0x102c, 3, 3));
        for (int row = 0; row < platform.Writes.Count; row++)
        {
            Assert.Equal((row, found && row == 0 ? 3 : 0), platform.Writes[row]);
            bool expected = (found && row == 0)
                || ((states[Math.Min(row, states.Length - 1)] & ~0xf) == 0);
            if (failure is not null && row == 1)
            {
                expected = false;
            }

            Assert.Equal(expected, sentRows.Contains(row));
        }

        Assert.Null(fixture.CallbackFailure);
    }
}
