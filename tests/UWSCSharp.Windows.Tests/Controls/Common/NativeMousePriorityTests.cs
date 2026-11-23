using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeMousePriorityTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : OwnedListSelectionPlatform
    {
        internal List<string> Events { get; } = [];

        public override void Priority(uint value)
        {
            Events.Add("priority" + value);
            if (value == 0x100 && failure == "raise")
            {
                throw new InvalidOperationException("priority callback");
            }
        }

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            Events.Add("post" + message);
            if (message is 0x201 or 0x204)
            {
                if (failure == "cancel")
                {
                    cancel.Cancel();
                }

                if (failure == "post")
                {
                    throw new InvalidOperationException("post callback");
                }
            }
        }
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(12288)]
    public void PriorityBracketsEntireMouseSequence(int kind)
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, null);
        NativeControlMouse.Post(1, kind, new(12, 34), platform, default);
        bool right = (kind & 4096) != 0;
        List<string> expected = ["priority256", right ? "post516" : "post513",
            right ? "post517" : "post514"];
        if (right)
        {
            expected.Add("post517");
        }

        if ((kind & 8192) != 0)
        {
            expected.Add(right ? "post518" : "post515");
            expected.Add(right ? "post517" : "post514");
            if (right)
            {
                expected.Add("post517");
            }
        }

        expected.Add("priority32");
        Assert.Equal(expected, platform.Events);
    }

    [Theory]
    [InlineData("raise")]
    [InlineData("post")]
    [InlineData("cancel")]
    public void FailureStillRequestsUpAndNormalPriority(string failure)
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure);
        if (failure == "cancel")
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeControlMouse.Post(1, 2048, new(12, 34), platform, cancel.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeControlMouse.Post(1, 2048, new(12, 34), platform, default));
        }

        string[] expected = failure == "raise" ? ["priority256", "priority32"]
            : ["priority256", "post513", "post514", "priority32"];
        Assert.Equal(expected, platform.Events);
    }

    [Fact]
    public void PrecancelDoesNotChangePriority()
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, null);
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => NativeControlMouse.Post(
            1, 2048, new(1, 2), platform, cancel.Token));
        Assert.Empty(platform.Events);
    }

    [Fact]
    public void CoordinateOverflowDoesNotChangePriority()
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, null);
        Assert.Throws<OverflowException>(() => NativeControlMouse.Post(
            1, 2048, new(1, -1), platform, default));
        Assert.Empty(platform.Events);
    }
}
