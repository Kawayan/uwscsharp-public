using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData("Save(&S)...", "save", false, true)]
    [InlineData("保存\tCtrl+S", "保存", false, true)]
    [InlineData("ＡＢＣ", "ABC", false, true)]
    [InlineData("カタカナ", "ｶﾀｶﾅ", false, true)]
    [InlineData("é", "É", false, false)]
    [InlineData("ａｂｃ", "ABC", false, false)]
    [InlineData("Save document", "doc", true, true)]
    [InlineData("Save document", "doc", false, false)]
    [InlineData("ＡＢＣ", "B", true, true)]
    [InlineData("保存", "", true, true)]
    public async Task PublicButtonNamesUseAsciiMnemonicAndNarrowComparison(
        string name, string desired, bool partial, bool found)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", name, 6, 10);
        var platform = new Platform();
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(found ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String(desired), Value.Numeric(1 | 512
                | (partial ? 256 : 0) | (found ? 0 : 536870912))], default));
        Assert.Equal(found ? 2 : 0, platform.Posts.Count);
        if (!partial && found)
        {
            Native.SendMessageW(button, 0xf1, 2, 0);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("CHKBTN",
                [id, Value.String(desired)], default));
        }
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3)]
    [InlineData(-1, 0)]
    [InlineData(-2, 1)]
    [InlineData(-3, 2)]
    [InlineData(-4)]
    public async Task NumberZeroVisitsAllAndNegativeIncludesDisabled(
        int number, params int[] expected)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint[] buttons =
        [
            fixture.Child("BUTTON", "same", 3, 10),
            fixture.Child("BUTTON", "same", 3 | 0x08000000, 40),
            fixture.Child("BUTTON", "same", 3, 70)
        ];
        var platform = new Platform();
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected.Length == 0 ? 0 : 1),
            await session.InvokeAsync("CLKITEM", [id, Value.String("same"),
                Value.Numeric(1 | 512 | (expected.Length == 0 ? 536870912 : 0)),
                Value.Numeric(1), Value.Numeric(number)], default));
        Assert.Equal(expected.SelectMany(index => new[] { buttons[index], buttons[index] }),
            platform.PostWindows);
    }

    [Fact]
    public async Task ZeroNumberCancellationStopsAfterFirstButton()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("BUTTON", "same", 3, 10);
        fixture.Child("BUTTON", "same", 3, 40);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { Posting = cancellation.Cancel };
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("same"),
                Value.Numeric(1 | 512), Value.Numeric(1), Value.Numeric(0)], cancellation.Token));
        Assert.All(platform.PostWindows, window => Assert.Equal(first, window));
        Assert.Equal(2, platform.PostWindows.Count);
    }
}
