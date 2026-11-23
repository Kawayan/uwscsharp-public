using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 記録器は起動せず、再生先もTargetへ差し替えて入力を送信しません。
[Trait("Category", "Headless")]
public sealed class RecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactRecordingPreservesDragAndClickCoordinates(bool compact)
    {
        var buffer = new RecordingBuffer([], 0)
        {
            OmitIdleTimeAndMouseMoves = compact
        };
        buffer.Mouse(-1, 1, 10, 20, 0, false);
        buffer.Mouse(0, 1, 30, 40, 10, false);
        buffer.Mouse(-1, 1, 50, 60, 20, false);
        buffer.Mouse(0, 2, 70, 80, 30, false);
        buffer.Mouse(-1, 1, 90, 100, 40, false);
        buffer.Wheel(5, 120, 110, 120, 50, false);
        buffer.Key(65, 1, 60, false);
        string source = BasicRecorder.Serialize(buffer.Events, compact);
        Assert.Equal(!compact, source.Contains("SLEEP("));
        var target = new Target();
        await new Interpreter(windows: target).RunAsync(source, _ => { });
        string[] expected = compact
            ? ["BTN:0,1,30,40", "MMV:50,60", "BTN:0,2,70,80",
                "BTN:5,1,110,120", "KBD:65,1", "KBD:65,2"]
            : ["MMV:10,20", "BTN:0,1,30,40", "MMV:50,60",
                "BTN:0,2,70,80", "MMV:90,100", "BTN:5,1,110,120",
                "KBD:65,1", "KBD:65,2"];
        Assert.Equal(expected, target.Calls);
        Assert.True(target.Released);
    }

    [Fact]
    public void OmittedMouseMovesDoNotConsumeBufferCapacity()
    {
        var buffer = new RecordingBuffer([], 0, limit: 2)
        {
            OmitIdleTimeAndMouseMoves = true
        };
        for (int i = 0; i < 100_000; i++)
        {
            buffer.Mouse(-1, 1, i, i, i, false);
        }

        buffer.Mouse(0, 1, 10, 20, 100_000, false);
        buffer.Mouse(0, 2, 10, 20, 100_001, false);
        Assert.False(buffer.Overflowed);
        Assert.Equal(2, buffer.Events.Count);
    }

    [Fact]
    public void CompactRecordingKeepsAcwAndReleasesAfterRecovery()
    {
        var window = new RecordedWindow(1, "文書", "Editor", 0, 0, 800, 600);
        var buffer = new RecordingBuffer([], 0)
        {
            OmitIdleTimeAndMouseMoves = true
        };
        buffer.Mouse(0, 1, 10, 20, 0, false, window);
        buffer.Recover(10, [], []);
        buffer.Mouse(-1, 1, 30, 40, 20, false);
        buffer.Key(65, 1, 30, false, window);
        string source = BasicRecorder.Serialize(buffer.Events, true);
        Assert.Equal(string.Join(Environment.NewLine,
            window.Command(), "BTN(0,1,10,20)", "BTN(0,2)",
            "KBD(65,1)", "KBD(65,2)", ""), source);
    }

    [Fact]
    public async Task GeneratedScriptReplaysCoordinatesAndReleasesHeldInputs()
    {
        string source = BasicRecorder.Serialize(
        [
            new(1, 0, 1, -20, 30, 100),
            new(2, 1, 1, -20, 30, 110),
            new(0, 65, 1, 0, 0, 120)
        ]);
        var target = new Target();
        await new Interpreter(windows: target).RunAsync(source, _ => { });
        Assert.Equal(
        [
            "MMV:-20,30", "BTN:1,1,-20,30", "KBD:65,1",
            "KBD:65,2", "BTN:1,2"
        ], target.Calls);
        Assert.True(target.Released);
    }

    [Fact]
    public void CompletedKeyDoesNotGetDuplicateRelease()
    {
        string source = BasicRecorder.Serialize(
        [
            new(0, 65, 1, 0, 0, 10),
            new(0, 65, 2, 0, 0, 10)
        ]);
        Assert.Equal("KBD(65,1)" + Environment.NewLine
            + "KBD(65,2)" + Environment.NewLine, source);
    }

    [Fact]
    public async Task RecoverySegmentReplaysReleaseBeforeNextInputWithoutPointerMove()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, 100, 200, 1, false);
        buffer.Recover(2, [], []);
        buffer.Key(65, 1, 3, false);
        buffer.Key(65, 2, 4, false);
        string source = "// 警告: 入力欠落の可能性があります\n"
            + BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        var target = new Target();
        await new Interpreter(windows: target).RunAsync(source, _ => { });
        Assert.Equal(["BTN:0,1,100,200", "BTN:0,2", "KBD:65,1", "KBD:65,2"], target.Calls);
        Assert.True(target.Released);
    }

    private sealed class Target : IWindowsAutomation,
        IWindowsAutomationSession
    {
        public List<string> Calls { get; } = [];

        public bool Released
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Calls.Add(name + ":" + string.Join(",",
                arguments.Select(value => value.Display())));
            return ValueTask.FromResult(new Value(ValueKind.Empty));
        }

        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
