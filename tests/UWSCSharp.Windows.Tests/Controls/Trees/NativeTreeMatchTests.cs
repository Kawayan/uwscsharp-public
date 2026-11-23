using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeMatchTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal List<string> Events { get; } = [];
        internal int Selections;
        internal int Tens;
        internal bool Restored;

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root, Buffers);

        public override bool IsVisible(nint root) => false;

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (root == 77)
            {
                Assert.True(Buffers.Released);
                Assert.True(Buffers.Closed);
                Restored = true;
            }
        }

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (message == 0)
            {
                return 1;
            }

            nint result = base.Send(target, message, parameter, data, timed, timeout, flags);
            if (message == 0x110b)
            {
                Selections++;
                Events.Add("select");
            }

            if (message is 0x100 or 0x101 && parameter == 107)
            {
                Events.Add(message == 0x100 ? "add-down" : "add-up");
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
            }

            return result;
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (milliseconds == 10)
            {
                Tens++;
                if ((failure == "second" && Selections == 2)
                    || (failure == "caller" && Selections == 3))
                {
                    cancel.Cancel();
                }
            }

            token.ThrowIfCancellationRequested();
        }

        public override void Pump(CancellationToken token) => token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SavedFlagControlsRepeatsIndependentlyOfCurrentNativeExpansion(
        bool query, bool reportedExpanded, bool nativeExpanded)
    {
        await Run(query, reportedExpanded, nativeExpanded, null);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("throw_key")]
    [InlineData("second")]
    public async Task InterruptedRepeatsKeepSelectionAndExpansion(string failure)
    {
        await Run(true, false, false, failure);
    }

    private static async Task Run(bool query, bool reportedExpanded,
        bool nativeExpanded, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint item = TreeControlTests.Add(tree, 0, "target");
        TreeControlTests.Add(tree, item, "child");
        ViewCheckTests.SetState(tree, item, false, 2);
        if (nativeExpanded)
        {
            Native.SendMessageW(tree, 0x1102, 2, item);
        }

        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure);
        int nameReads = 0;
        int stateReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e)
            {
                if (Marshal.ReadInt32(pointer) == 0x41)
                {
                    nameReads++;
                }
                else
                {
                    stateReads++;
                }
            }

            return null;
        }, after: (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                // getterが戻った後だけ変更し、nativeへ不正なhandleを渡しません。
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8,
                    reportedExpanded ? 0x20 : 0);
            }
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = query ? [id, Value.String("target")]
            : [id, Value.String("target"), Value.Numeric(16)];
        if (failure == "throw_key")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(query ? "CHKBTN" : "CLKITEM", args, cancel.Token));
        }
        else if (failure is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(query ? "CHKBTN" : "CLKITEM", args, cancel.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
                query ? "CHKBTN" : "CLKITEM", args, default));
        }

        Assert.Equal(1, nameReads);
        Assert.Equal(failure is not null ? 0 : 1, stateReads);
        if (failure is null)
        {
            Assert.Equal(reportedExpanded ? ["select"]
                : new[] { "select", "add-down", "add-up", "select" }, platform.Events);
            Assert.Equal(reportedExpanded ? 10 : 20, platform.Tens);
        }
        else
        {
            Assert.Equal(failure is "key" or "throw_key" ? 1 : failure == "second" ? 2 : 3,
                platform.Selections);
            Assert.Contains("add-up", platform.Events);
        }

        Assert.Equal(item, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Equal(nativeExpanded || !reportedExpanded,
            (Native.SendMessageW(tree, 0x1127, item, 0x20) & 0x20) != 0);
        Assert.True(platform.Restored);
        Assert.Null(fixture.CallbackFailure);
    }
}
