using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarNumberTests
{
    [Theory]
    [InlineData(null, false, 703)]
    [InlineData(0, false, 703)]
    [InlineData(1, false, 703)]
    [InlineData(2, false, 704)]
    [InlineData(-1, false, 701)]
    [InlineData(-2, false, 702)]
    [InlineData(-3, false, 703)]
    [InlineData(-4, false, 704)]
    [InlineData(-2, true, 702)]
    public async Task CountsAcrossControlsAndNegativeIncludesDisabledControl(
        int? number, bool hidden, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint disabled = ToolbarControlTests.Create(fixture, 10, 0x08000000);
        ToolbarControlTests.Add(disabled, "target", 700, state: 0);
        ToolbarControlTests.Add(disabled, "target", 705, state: 12);
        ToolbarControlTests.Add(disabled, "target", 701);
        ToolbarControlTests.Add(disabled, "target", 702);
        nint enabled = ToolbarControlTests.Create(fixture, 60);
        ToolbarControlTests.Add(enabled, "target", 703);
        ToolbarControlTests.Add(enabled, "target", 704);
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        int expectedIndex = expected <= 702 ? expected - 699 : expected - 703;
        nint selected = expected <= 702 ? disabled : enabled;
        using var selectedHook = new ToolbarClickTextTests.Interceptor(selected, (message, index, _) =>
        {
            if (message == 0x41d)
            {
                Assert.Equal(expectedIndex, index);
            }

            return null;
        });
        List<int> commands = [];
        fixture.Command = (_, command) => commands.Add(command);
        var platform = new ToolbarOperationTests.RecordingPlatform();
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        List<Value> args = [id, Value.String("target"), Value.Numeric(64), Value.Numeric(1)];
        if (number.HasValue)
        {
            args.Add(Value.Numeric(number.Value));
        }

        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        Assert.Equal(2, platform.Posts.Count);
        Assert.All(platform.Posts, post => Assert.Equal(expected <= 702 ? disabled : enabled, post.Target));
        Assert.Empty(commands);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task MinimumIntegerFailsBeforeReadingButtons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701);
        int reads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, _) =>
        {
            reads += message == 0x417 ? 1 : 0;
            return null;
        });
        var platform = new ToolbarOperationTests.RecordingPlatform();
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () => await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(64), Value.Numeric(1), Value.Numeric(int.MinValue)], default));
        Assert.Equal(0, reads);
        Assert.Null(fixture.CallbackFailure);
    }
}
