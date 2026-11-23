using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewDestructionTests
{
    private sealed class Platform : ToolbarOperationTests.RecordingPlatform
    {
        internal int Preparations;

        public override void Activate(nint window, CancellationToken token)
        {
            if (window != 77)
            {
                // VIEW queryにはbuffer所有中の事前準備もあります。
                Assert.False(Buffers.Released);
                Assert.False(Buffers.Closed);
                Preparations++;
                return;
            }

            base.Activate(window, token);
        }

        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags) => 0;
    }

    [Theory]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("second-point", false)]
    [InlineData("second-point", true)]
    [InlineData("third-point", false)]
    [InlineData("post", false)]
    [InlineData("cancel-post", false)]
    [InlineData("restore", false)]
    public async Task DestroyedViewWithZeroGettersKeepsRequestResultAndReleasesOwner(string stage, bool query)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        using var cancel = new CancellationTokenSource();
        int points = 0;
        int restores = 0;
        bool destroyed = false;
        var platform = new Platform
        {
            Restoring = () =>
            {
                restores++;
                if (stage == "restore")
                {
                    throw new InvalidOperationException("復元失敗");
                }
            },
            Posting = message =>
            {
                if (stage is "post" or "cancel-post" && message == 0x201)
                {
                    destroyed = Native.DestroyWindow(view);
                    Assert.True(destroyed);
                    if (stage == "cancel-post")
                    {
                        cancel.Cancel();
                    }
                }
            },
        };
        using var hook = new ToolbarClickTextTests.Interceptor(view, (_, _, _) => null,
            after: (message, _, _) =>
            {
                points += message == 0x1010 ? 1 : 0;
                bool destroy = stage == "name" && message == 0x1073
                    || stage == "second-point" && message == 0x1010 && points == 2
                    || stage is "third-point" or "restore" && message == 0x1010 && points == 3;
                if (destroy && !destroyed)
                {
                    destroyed = Native.DestroyWindow(view);
                    Assert.True(destroyed);
                }
            });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        int kind = 32 | 512 | (stage is "post" or "cancel-post" ? 2048 : 0);
        Value[] args = query ? [id, Value.String("target")]
            : [id, Value.String("target"), Value.Numeric(kind)];
        if (stage == "cancel-post")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (stage == "restore")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, default));
        }
        else
        {
            Assert.Equal(Value.Numeric(query ? 0 : 1), await session.InvokeAsync(
                query ? "CHKBTN" : "CLKITEM", args, default));
        }

        Assert.True(destroyed);
        Assert.False(Native.IsWindow(view));
        Assert.True(Native.IsWindow(fixture.Root));
        Assert.Equal(1, restores);
        Assert.Equal(query ? 1 : 0, platform.Preparations);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        if (stage is "post" or "cancel-post")
        {
            Assert.Equal(new uint[] { 0x201, 0x202 }, platform.Posts.Select(post => post.Message));
            Assert.Equal("priority32", platform.Events.Last());
        }

        Assert.Null(fixture.CallbackFailure);
    }
}
