using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabOffTests
{
    private sealed class Platform : ToolbarOperationTests.RecordingPlatform
    {
        internal Native.Point? Moved;

        public override void Activate(nint root, CancellationToken token)
        {
            if (Buffers.Address != 0)
            {
                base.Activate(root, token);
            }
            else
            {
                Restoring?.Invoke();
            }
        }

        public override Native.Point ToScreen(nint target, Native.Point point) => new(110, -80);

        public override void MoveCursor(Native.Point point) => Moved = point;

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token) =>
            token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(true, 0, 1)]
    [InlineData(false, 1024, 1)]
    [InlineData(false, 2048, 1)]
    [InlineData(false, 3072, 1)]
    [InlineData(false, 256, 2)]
    [InlineData(false, 0, 0)]
    [InlineData(false, 0, -1)]
    [InlineData(false, 0, 2)]
    public async Task PublicScriptSelectsTabForEitherOnValue(bool on, int flags, int number)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "other", "target", "target");
        var platform = new Platform { Restoring = () => { } };
        List<int> codes = [];
        fixture.Notify = (_, pointer) =>
        {
            codes.Add(Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code);
            return 1;
        };
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, index, pointer) =>
        {
            if (message != 0x130a)
            {
                return null;
            }

            Assert.Equal(number == 2 ? 2 : 1, index);
            Marshal.WriteInt32(pointer, 10);
            Marshal.WriteInt32(pointer, 4, 20);
            Marshal.WriteInt32(pointer, 8, 21);
            Marshal.WriteInt32(pointer, 12, 35);
            return 0;
        });
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: platform)).RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            PRINT CLKITEM(id, "{(flags == 256 ? "tar" : "target")}", {8 | 512 | flags}, {(on ? "TRUE" : "FALSE")}, {number})
            """, output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(number == 2 ? 2 : 1, Native.SendMessageW(tab, 0x130b, 0, 0));
        Assert.Equal(new[] { -552, -552, -551 }, codes);
        Assert.Equal((flags & 2048) == 0 ? 0 : 2, platform.Posts.Count);
        if ((flags & 1024) != 0)
        {
            Assert.NotNull(platform.Moved);
            Assert.Equal((115, -73), (platform.Moved.Value.X, platform.Moved.Value.Y));
        }
        else
        {
            Assert.Null(platform.Moved);
        }

        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffPreservesCandidateContinuationAndCancellation(bool cancelAtChanged)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = TabControlTests.Create(fixture, 10, 0, "other", "target");
        nint second = TabControlTests.Create(fixture, 60, 0, "other", "target");
        using var cancel = new CancellationTokenSource();
        var platform = new Platform { Restoring = () => { } };
        List<nint> changed = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -551)
            {
                changed.Add(notice.Window);
                if (notice.Window == first)
                {
                    Native.SendMessageW(first, 0x130c, 0, 0);
                    if (cancelAtChanged)
                    {
                        cancel.Cancel();
                    }
                }
            }

            return 1;
        };
        List<string> output = [];
        var interpreter = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform));
        string source = $"""
            id = HNDTOID({(long)fixture.Root})
            PRINT CLKITEM(id, "target", CLK_TAB OR CLK_BACK, FALSE, 0)
            """;
        if (cancelAtChanged)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                interpreter.RunAsync(source, output.Add, cancel.Token));
            Assert.Empty(output);
        }
        else
        {
            await interpreter.RunAsync(source, output.Add);
            Assert.Equal(["1"], output);
        }

        Assert.Equal(cancelAtChanged ? new[] { first } : [first, second], changed);
        Assert.Equal(cancelAtChanged ? 0 : 1, Native.SendMessageW(second, 0x130b, 0, 0));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultNativeSearchAlsoSelectsTabWhenOff(bool script)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "other", "target");
        fixture.Notify = (_, _) => 1;
        var platform = new Platform { Restoring = () => { } };
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
            listSelectionPlatform: platform);
        if (script)
        {
            List<string> output = [];
            await new Interpreter(windows: automation).RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "target", CLK_API OR CLK_BACK, FALSE)
                """, output.Add);
            Assert.Equal(["1"], output);
        }
        else
        {
            await using IWindowsAutomationSession session = automation.CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(536870912 | 512), Value.Numeric(0)], default));
        }

        Assert.Equal(1, Native.SendMessageW(tab, 0x130b, 0, 0));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }
}
