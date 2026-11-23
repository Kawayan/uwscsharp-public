using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeButtonValueTests
{
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public async Task IndeterminateRequestMatchesStateTwoWithoutClick(int style, bool script)
    {
        await Verify(style, 2, 2, script);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task OutOfRangeRequestNormalizesToCheckedOne(int request)
    {
        await Verify(3, 1, request, true);
    }

    private static async Task Verify(int style, int initial, int request, bool script)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", style, 10);
        Native.SendMessageW(button, 0xf1, initial, 0);
        int clicks = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(button, (message, _, _) =>
        {
            clicks += message == 0xf5 ? 1 : 0;
            return null;
        });
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
            listSelectionPlatform: new OwnedListSelectionPlatform());
        if (script)
        {
            List<string> output = [];
            await new Interpreter(windows: automation).RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "target", CLK_BTN OR CLK_BACK, {request})
                """, output.Add);
            Assert.Equal(["1"], output);
        }
        else
        {
            await using IWindowsAutomationSession session = automation.CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(1 | 512), Value.Numeric(request)], default));
        }

        Assert.Equal(0, clicks);
        Assert.Equal(initial, Native.SendMessageW(button, 0xf0, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }
}
