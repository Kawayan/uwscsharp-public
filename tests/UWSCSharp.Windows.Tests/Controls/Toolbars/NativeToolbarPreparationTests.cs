using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarPreparationTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure, bool attach)
        : OwnedListSelectionPlatform
    {
        internal List<string> Events { get; } = [];
        internal nint Root;
        internal nint Target;
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root, Buffers);

        public override uint CurrentThread()
        {
            Events.Add("current");
            return base.CurrentThread();
        }

        public override uint WindowThread(nint window)
        {
            Events.Add(window == Root ? "root-thread" : "child-thread");
            return base.WindowThread(window);
        }

        public override bool Attach(uint target, uint current, bool enabled)
        {
            Events.Add(enabled ? "attach" : "detach");
            return attach;
        }

        public override void Foreground(nint window)
        {
            Assert.Equal(Root, window);
            Events.Add("foreground");
            if (failure == "foreground")
            {
                throw new InvalidOperationException("foreground callback");
            }
        }

        public override void Focus(nint window)
        {
            Assert.Equal(Target, window);
            Events.Add("focus");
            if (failure == "focus")
            {
                throw new InvalidOperationException("focus callback");
            }

            if (failure == "cancel")
            {
                cancel.Cancel();
            }
        }

        public override void Sleep(int milliseconds, CancellationToken token) => token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token) => token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, true, "foreground")]
    [InlineData(false, true, "focus")]
    [InlineData(false, true, "cancel")]
    public async Task SessionPreparesAfterRectangleAndDetachesOnFailure(
        bool back, bool attach, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701);
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure, attach) { Root = fixture.Root, Target = toolbar };
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, _) =>
        {
            if (message == 0x41d)
            {
                // operation入口の所有thread判定を除き、準備以降の順序を確認します。
                platform.Events.Clear();
                platform.Events.Add("rectangle");
            }

            if (message == 0x448)
            {
                platform.Events.Add("hot");
            }

            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(back ? 576 : 64), Value.Numeric(0)];
        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (failure is not null)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }

        List<string> expected = ["rectangle"];
        if (!back)
        {
            expected.AddRange(["current", "root-thread", "attach"]);
            if (attach)
            {
                expected.Add("foreground");
                if (failure != "foreground")
                {
                    expected.Add("focus");
                }

                expected.Add("detach");
            }
        }

        if (failure is null)
        {
            expected.AddRange(["hot", "hot"]);
        }

        Assert.Equal(expected, platform.Events);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }
}
