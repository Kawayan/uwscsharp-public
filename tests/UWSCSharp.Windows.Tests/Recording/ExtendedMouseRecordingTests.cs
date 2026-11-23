using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ExtendedMouseRecordingTests
{
    [Fact]
    public async Task ScriptAliasesReplayAndUnwindSideButtons()
    {
        var sent = new List<Native.Input>();
        var automation = new WindowsAutomation(sent.Add);
        await new Interpreter(windows: automation).RunAsync("""
            BTN(XBUTTON1,DOWN)
            BTN(WHEEL,0.5)
            BTN(XBUTTON2,DOWN)
            """, _ => { });
        Assert.Equal([0x80u, 0x800u, 0x80u, 0x100u, 0x100u],
            sent.Select(input => input.Data.Mouse.Flags));
        Assert.Equal([1u, 60u, 2u, 1u, 2u], sent.Select(input => input.Data.Mouse.Data));
    }

    [Fact]
    public void HookPayloadDecodesSignedDeltaAndBothSideButtons()
    {
        var buffer = new RecordingBuffer([], 0);
        BasicRecorder.RecordMouse(buffer, 0x20a, 0xffc40000, 10, 20, 1, false);
        BasicRecorder.RecordMouse(buffer, 0x20e, 0x00010000, 10, 20, 1, false);
        BasicRecorder.RecordMouse(buffer, 0x20b, 0x00030000, 10, 20, 1, false);
        BasicRecorder.RecordMouse(buffer, 0x20c, 0x00030000, 10, 20, 1, false);
        Assert.Equal([-60, 1, 1, 1, 2, 2], buffer.Events.Select(input => input.State));
        Assert.Equal([5, 6, 3, 4, 3, 4], buffer.Events.Select(input => input.Code));
    }

    [Theory]
    [InlineData(5, 0x20au)]
    [InlineData(6, 0x20eu)]
    public async Task DirectWheelPreservesSignedDeltaWithoutHeldInput(int button, uint message)
    {
        var backend = new DirectInputTests.Backend();
        var direct = new DirectInputSession(backend);
        await direct.MouseAsync(10, button, 0, new(2, 3), default, -1);
        (nint Window, uint Message, uint WParam, uint LParam) actual =
            Assert.Single(backend.Messages, value => value.Message == message);
        Assert.Equal(65535u, actual.WParam >> 16);
        Assert.False(direct.HasHeldInput);
    }

    [Fact]
    public void SerializesHighResolutionWheelsWithoutInventingRelease()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Wheel(5, 60, 10, 20, 1, false);
        buffer.Wheel(6, -1, 10, 20, 1, false);
        buffer.Mouse(3, 1, 10, 20, 1, false);
        buffer.Mouse(4, 1, 10, 20, 1, false);
        string source = BasicRecorder.Serialize(buffer.Events);
        Assert.Contains("BTN(5,0.5,10,20)", source);
        Assert.Contains("BTN(6,-0.008333333333333333,10,20)", source);
        Assert.EndsWith("BTN(3,2)" + Environment.NewLine + "BTN(4,2)" + Environment.NewLine, source);
        Assert.DoesNotContain("BTN(5,2)", source);
        Assert.DoesNotContain("BTN(6,2)", source);
    }

    [Fact]
    public void InitialHeldSideButtonAndExcludedWheelAreNotRecorded()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Begin([], [3]);
        buffer.Wheel(5, 120, 0, 0, 1, false);
        buffer.Mouse(3, 2, 0, 0, 2, false);
        buffer.Wheel(5, 120, 0, 0, 3, true);
        buffer.Wheel(5, 0, 0, 0, 4, false);
        Assert.Empty(buffer.Events);
        buffer.Mouse(4, 1, 0, 0, 5, false);
        buffer.Recover(6, [], []);
        Assert.Contains("BTN(4,2)", BasicRecorder.Serialize(buffer.Events));
    }

    [Theory]
    [InlineData(5, 0.5, 0x800u, 60u)]
    [InlineData(6, -0.5, 0x1000u, 4294967236u)]
    [InlineData(5, -0.008333333333333333, 0x800u, 4294967295u)]
    public async Task ReplayPreservesRawWheelDelta(int button, double notches, uint flags, uint delta)
    {
        var sent = new List<Native.Input>();
        await using IWindowsAutomationSession session = new WindowsAutomation(sent.Add).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(button), Value.Numeric(notches)], default);
        Native.MouseInput actual = Assert.Single(sent).Data.Mouse;
        Assert.Equal(flags, actual.Flags);
        Assert.Equal(delta, actual.Data);
    }

    [Theory]
    [InlineData(3, 1u)]
    [InlineData(4, 2u)]
    public async Task SideButtonReplayAndDisposeUseXInput(int button, uint data)
    {
        var sent = new List<Native.Input>();
        IWindowsAutomationSession session = new WindowsAutomation(sent.Add).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(button), Value.Numeric(1)], default);
        Assert.False(session.CanPause);
        await session.DisposeAsync();
        Assert.Equal([0x80u, 0x100u], sent.Select(input => input.Data.Mouse.Flags));
        Assert.All(sent, input => Assert.Equal(data, input.Data.Mouse.Data));
    }

    [Theory]
    [InlineData(3, 1u)]
    [InlineData(4, 2u)]
    public async Task DirectSideButtonMessagesAndReleaseKeepIdentity(int button, uint identity)
    {
        var backend = new DirectInputTests.Backend();
        var direct = new DirectInputSession(backend);
        await direct.MouseAsync(10, button, 1, new(2, 3), default);
        var errors = new List<Exception>();
        direct.Release(errors);
        Assert.Empty(errors);
        (nint Window, uint Message, uint WParam, uint LParam)[] messages =
            [.. backend.Messages.Where(message => message.Message is 0x20b or 0x20c)];
        Assert.Equal([0x20bu, 0x20cu], messages.Select(message => message.Message));
        Assert.All(messages, message => Assert.Equal(identity, message.WParam >> 16));
        Assert.False(direct.HasHeldInput);
    }
}
