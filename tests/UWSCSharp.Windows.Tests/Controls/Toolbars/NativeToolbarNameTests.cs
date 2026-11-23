using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarNameTests
{
    [Theory]
    [InlineData("&Run...", "run", false, true)]
    [InlineData("ＡＢＣ", "ABC", false, true)]
    [InlineData("ＡＢＣsuffix", "ABC", true, true)]
    [InlineData("prefix Run suffix", "run", true, true)]
    [InlineData("prefix Run suffix", "run", false, false)]
    [InlineData("ä", "Ä", false, false)]
    [InlineData("Run\tShortcut", "run", false, true)]
    [InlineData("Run", "", true, true)]
    public void MatchesNormalizedNameWithAsciiAndHalfWidth(string text,
        string name, bool partial, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, text, 701);
        Assert.Equal((expected ? 1 : 0, expected, expected),
            NativeToolbarControl.Click(toolbar, name, 1, false, default, partial: partial,
            platform: new OwnedListSelectionPlatform()));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortMatchesSecondOccurrenceThroughSession(bool partial)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "first Run suffix", 701);
        ToolbarControlTests.Add(toolbar, "second Run suffix", 702);
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, index, _) =>
        {
            if (message == 0x41d)
            {
                Assert.Equal(1, index);
            }

            return null;
        });
        List<int> commands = [];
        fixture.Command = (_, command) => commands.Add(command);
        var platform = new ToolbarOperationTests.RecordingPlatform();
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        string name = partial ? "run" : "second run suffix";
        int number = partial ? 2 : 1;
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(name), Value.Numeric(partial ? 320 : 64),
                Value.Numeric(1), Value.Numeric(number)], default));
        Assert.Equal(2, platform.Posts.Count);
        Assert.Empty(commands);
        Assert.Null(fixture.CallbackFailure);
    }
}
