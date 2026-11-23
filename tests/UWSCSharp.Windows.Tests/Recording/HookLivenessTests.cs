using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class HookLivenessTests
{
    [Fact]
    public void IdleDoesNotMeanHookWasRemoved()
    {
        var health = new HookLiveness();
        Assert.Equal(0, health.Missing(86_400_000));
        health.Hook(0, 100);
        Assert.Equal(0, health.Missing(86_400_000));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void RawInputWithoutHookTriggersOnlyThatKindAfterGrace(int kind, int missing)
    {
        var health = new HookLiveness();
        health.Raw(kind, 100);
        health.Raw(kind, 500);
        Assert.Equal(0, health.Missing(1099));
        Assert.Equal(missing, health.Missing(1100));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HookBeforeOrAfterRawInputIsHealthy(bool hookFirst)
    {
        var health = new HookLiveness();
        if (hookFirst)
        {
            health.Hook(1, 100);
            health.Raw(1, 101);
        }
        else
        {
            health.Raw(1, 100);
            health.Hook(1, 101);
        }
        Assert.Equal(0, health.Missing(2000));
    }

    [Fact]
    public void OtherKindCannotMaskLostHookAndResetRequiresNewEvidence()
    {
        var health = new HookLiveness();
        health.Raw(0, 0);
        health.Hook(1, 1000);
        Assert.Equal(1, health.Missing(1000));
        health.Reset();
        Assert.Equal(0, health.Missing(10000));
        health.Raw(1, 1001, 10000);
        Assert.Equal(0, health.Missing(11000));
        health.Raw(1, 10000);
        Assert.Equal(2, health.Missing(11000));
    }

    [Fact]
    public void OldHookResponseCannotMaskLaterRawInput()
    {
        var health = new HookLiveness();
        health.Hook(0, 100);
        health.Raw(0, 301);
        Assert.Equal(1, health.Missing(1301));
    }

    [Fact]
    public void DelayedQueuedRawInputDoesNotMakeHealthyHookLookLost()
    {
        var health = new HookLiveness();
        health.Hook(1, 100);
        health.Raw(1, 101, 10000);
        Assert.Equal(0, health.Missing(11000));
    }

    [Fact]
    public void RecoveryClosesHeldInputsWithoutMovingPointerAndWaitsForRelease()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(160, 1, 1, false);
        buffer.Key(65, 1, 2, false);
        buffer.Mouse(0, 1, 100, 200, 3, false);
        buffer.Recover(4, [160, 65], [0]);
        Assert.True(buffer.WaitingForInitialRelease);
        string boundary = BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        Assert.Contains("KBD(160,2)", boundary);
        Assert.Contains("KBD(65,2)", boundary);
        Assert.Contains("BTN(0,2)", boundary);
        Assert.DoesNotContain("BTN(0,2,0,0)", boundary);
        buffer.Key(65, 2, 5, false);
        buffer.Key(160, 2, 6, false);
        buffer.Mouse(0, 2, 100, 200, 7, false);
        Assert.False(buffer.WaitingForInitialRelease);
        buffer.Key(66, 1, 8, false);
        buffer.Key(66, 2, 9, false);
        string source = BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        Assert.Contains("KBD(66,1)", source);
        Assert.Contains("KBD(66,2)", source);
    }

    [Fact]
    public void RecoveryCompletesPendingModifierBeforeNewSegment()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(160, 1, 1, false);
        buffer.Recover(2, [], []);
        buffer.Key(65, 1, 3, false);
        string source = BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        Assert.True(source.IndexOf("KBD(160,2)", StringComparison.Ordinal)
            < source.IndexOf("KBD(65,1)", StringComparison.Ordinal));
    }
}
