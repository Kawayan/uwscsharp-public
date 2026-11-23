using Xunit;

namespace UWSCSharp.Windows.Tests;

// バッファと文字列化だけを使い、実画面・フック・入力送信は呼びません。
[Trait("Category", "Headless")]
public sealed class RecordingBoundaryTests
{
    [Fact]
    public void SaturatedModifierChordsDoNotAllocatePerCycle()
    {
        var buffer = new RecordingBuffer([], 0, limit: 1);
        buffer.Mouse(-1, 1, 0, 0, 0, false);
        for (int index = 0; index < 10; index++)
        {
            Chord(buffer, index);
        }
        long allocated = MinimumAllocatedBytes(round =>
        {
            for (int index = 0; index < 100_000; index++)
            {
                Chord(buffer, round * 100_000L + index);
            }
        });
        Assert.InRange(allocated, 0, 1024);
        Assert.True(buffer.Overflowed);
        Assert.Single(buffer.Events);
    }

    /// <summary>
    /// 同じ処理の割り当て量を複数回測り、最小値を返します。
    /// 段階的コンパイルなどランタイムの単発の割り当てが計測区間に入る場合を除外します。
    /// 入力ごとの割り当てがあれば全回で予算を超えるため、検査の意図は保たれます。
    /// </summary>
    private static long MinimumAllocatedBytes(Action<int> round)
    {
        const int Rounds = 3;
        long minimum = long.MaxValue;
        for (int index = 0; index < Rounds; index++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            round(index);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimum = Math.Min(minimum, allocated);
        }

        return minimum;
    }

    private static void Chord(RecordingBuffer buffer, long time)
    {
        buffer.Key(160, 1, time, false);
        buffer.Key(65, 1, time, false);
        buffer.Key(65, 2, time, false);
        buffer.Key(160, 2, time, false);
    }

    [Fact]
    public void SaturatedMovementBufferDoesNotAllocatePerEvent()
    {
        var buffer = new RecordingBuffer([], 0);
        for (int index = 0; index < 50_001; index++)
        {
            buffer.Mouse(-1, 1, index, -index, index, false);
        }

        long allocated = MinimumAllocatedBytes(round =>
        {
            long offset = 50_001L + round * 2_000_000L;
            for (int index = 0; index < 2_000_000; index++)
            {
                buffer.Mouse(-1, 1, index, -index, index + offset, false);
            }
        });
        Assert.True(buffer.Overflowed);
        Assert.Equal(50_000, buffer.Events.Count);
        Assert.Equal(49_999, buffer.Events[^1].X);
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    public void SaturatedMixedInputPreservesPrefixAndReleasesHeldInputs()
    {
        var buffer = new RecordingBuffer([121], 2, limit: 5);
        buffer.Key(162, 1, 0, false);
        buffer.Key(65, 1, 1, false);
        buffer.Mouse(0, 1, 10, 20, 2, false);
        buffer.Mouse(-1, 1, 11, 21, 3, false);
        buffer.Mouse(-1, 1, 12, 22, 4, false);
        BasicRecorder.InputEvent[] prefix = [.. buffer.Events];
        long allocated = MinimumAllocatedBytes(round =>
        {
            for (int index = 0; index < 100_000; index++)
            {
                long time = 5L + (round * 100_000L + index) * 4L;
                buffer.Key(65, 2, time, false);
                buffer.Key(65, 1, time + 1, false);
                buffer.Mouse(0, 2, 10, 20, time + 2, false);
                buffer.Mouse(0, 1, 10, 20, time + 3, false);
            }
        });
        buffer.Complete();
        Assert.True(buffer.Overflowed);
        Assert.InRange(allocated, 0, 1_024);
        Assert.Equal(prefix, buffer.Events);
        string script = BasicRecorder.Serialize(buffer.Events);
        Assert.Contains("KBD(162,2)", script);
        Assert.Contains("KBD(65,2)", script);
        Assert.Contains("BTN(0,2)", script);
    }

    [Fact]
    public void IgnoredInjectedUpDoesNotLeaveInitialArmingStuck()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Begin([160], [0]);
        buffer.ReleaseInitialKey(160);
        buffer.ReleaseInitialButton(0);
        buffer.Key(65, 1, 1, false);
        buffer.Key(65, 2, 2, false);
        Assert.Equal([65, 65], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void InputsPressedWhileArmingMustAlsoBeReleased()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Begin([160], []);
        buffer.Key(162, 1, 1, false);
        buffer.Key(160, 2, 2, false);
        buffer.Key(65, 1, 3, false);
        buffer.Key(65, 2, 4, false);
        buffer.Key(162, 2, 5, false);
        Assert.Empty(buffer.Events);
        buffer.Key(66, 1, 6, false);
        buffer.Key(66, 2, 7, false);
        Assert.Equal([66, 66], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void DeferredModifierKeepsOrderWhenTimestampsAreEqual()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(162, 1, 1, false);
        buffer.Mouse(-1, 1, 10, 20, 1, false);
        buffer.Key(65, 1, 1, false);
        buffer.Complete();
        string script = BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        Assert.StartsWith("KBD(162,1)" + Environment.NewLine
            + "MMV(10,20)" + Environment.NewLine
            + "KBD(65,1)" + Environment.NewLine, script);
    }

    [Fact]
    public void ModifierHeldAtStopIsReleasedButControlPrefixIsDiscarded()
    {
        var buffer = new RecordingBuffer([121], 3);
        buffer.Key(160, 1, 1, false);
        buffer.Complete();
        buffer.Complete();
        string script = BasicRecorder.Serialize(buffer.Events);
        Assert.Equal("KBD(160,1)" + Environment.NewLine
            + "KBD(160,2)" + Environment.NewLine, script);
        var controlled = new RecordingBuffer([121], 3);
        controlled.Key(162, 1, 1, false);
        controlled.Key(164, 1, 2, false);
        controlled.Key(121, 1, 3, false);
        controlled.Complete();
        Assert.Empty(controlled.Events);
    }

    [Fact]
    public void RecordingWaitsUntilInitiallyHeldInputsAreReleased()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Begin([162], [0]);
        buffer.Key(65, 1, 1, false);
        buffer.Key(65, 2, 2, false);
        buffer.Key(162, 2, 3, false);
        buffer.Mouse(0, 2, 0, 0, 4, false);
        Assert.Empty(buffer.Events);
        buffer.Key(65, 1, 5, false);
        buffer.Key(65, 2, 6, false);
        Assert.Equal([65, 65], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void SelfWindowDownAndOrphanUpAreIgnoredButForeignUpIsBalanced()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(65, 2, 1, false);
        buffer.Key(66, 1, 2, true);
        buffer.Key(66, 1, 3, false);
        buffer.Key(66, 2, 4, false);
        buffer.Key(67, 1, 5, false);
        buffer.Key(67, 2, 6, true);
        Assert.Equal([67, 67], buffer.Events.Select(input => input.Code));
        Assert.Equal([1, 2], buffer.Events.Select(input => input.State));
    }

    [Fact]
    public void DragReleaseOverSelfWindowIsRetained()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, -20, 30, 1, false);
        buffer.Mouse(0, 2, 10, 10, 2, true);
        buffer.Mouse(1, 1, 10, 10, 3, true);
        buffer.Mouse(1, 2, 100, 100, 4, false);
        Assert.Equal([0, 0], buffer.Events.Select(input => input.Code));
        Assert.Equal([1, 2], buffer.Events.Select(input => input.State));
    }

    [Fact]
    public void ControlChordAndItsUnusedModifiersAreExcluded()
    {
        var buffer = new RecordingBuffer([121], 3);
        buffer.Key(162, 1, 1, false);
        buffer.Key(164, 1, 2, false);
        buffer.Key(121, 1, 3, false);
        buffer.Key(121, 2, 4, false);
        buffer.Key(164, 2, 5, false);
        buffer.Key(162, 2, 6, false);
        Assert.Empty(buffer.Events);
        buffer.Key(121, 1, 7, false);
        buffer.Key(121, 2, 8, false);
        Assert.Equal([121, 121], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void ModifierUsedForRealInputIsNotDiscardedByLaterControlChord()
    {
        var buffer = new RecordingBuffer([121], 2);
        buffer.Key(162, 1, 1, false);
        buffer.Key(65, 1, 2, false);
        buffer.Key(65, 2, 3, false);
        buffer.Key(121, 1, 4, false);
        buffer.Key(121, 2, 5, false);
        buffer.Key(162, 2, 6, false);
        Assert.Equal([162, 65, 65, 162], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void StandaloneModifierAndOverlappingModifiersArePreserved()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(162, 1, 1, false);
        buffer.Key(160, 1, 2, false);
        buffer.Key(162, 2, 3, false);
        buffer.Key(160, 2, 4, false);
        Assert.Equal([162, 160, 162, 160], buffer.Events.Select(input => input.Code));
    }

    [Fact]
    public void OverflowStillProducesBalancedRecordedPrefix()
    {
        var buffer = new RecordingBuffer([], 0, limit: 1);
        buffer.Key(65, 1, 1, false);
        buffer.Key(65, 2, 2, false);
        Assert.True(buffer.Overflowed);
        string script = BasicRecorder.Serialize(buffer.Events);
        Assert.Equal("KBD(65,1)" + Environment.NewLine
            + "KBD(65,2)" + Environment.NewLine, script);
    }
}
