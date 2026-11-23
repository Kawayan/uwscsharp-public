using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeButtonApiTests
{
    private static Value N(int value) => Value.Numeric(value);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task ApiThreeStateButtonUsesMessagesAndVerifiesState(int before, int desired)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "check", 6, 10);
        Native.SendMessageW(button, 0xf1, before, 0);
        int notifications = 0;
        fixture.Clicked = (_, _) => notifications++;
        await using IWindowsAutomationSession session = ApiSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("check"), N(1 | 512 | 536870912), N(desired)], default));
        Assert.Equal((nint)desired, Native.SendMessageW(button, 0xf0, 0, 0));
        Assert.Equal((desired - before + 3) % 3, notifications);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task ApiPushButtonDoesNotCreateAccessibilityBackend(int request, int expected)
    {
        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "push", 0, 10);
        int notifications = 0;
        fixture.Clicked = (_, _) => notifications++;
        await using IWindowsAutomationSession session = ApiSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("push"), N(1 | 512 | 536870912), N(request)], default));
        Assert.Equal(expected, notifications);
    }

    [Fact]
    public async Task OpaqueOwnerDrawDoesNotGuessStateOrClick()
    {
        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "opaque", 11, 10);
        int notifications = 0;
        fixture.Clicked = (_, _) => notifications++;
        await using IWindowsAutomationSession session = ApiSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("opaque"), N(1 | 512 | 536870912)], default));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task RejectedStateDoesNotRepeatClickOrFallback()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "check", 6, 10);
        int notifications = 0;
        fixture.Clicked = (_, _) =>
        {
            notifications++;
            Native.SendMessageW(button, 0xf1, 0, 0);
        };
        await using IWindowsAutomationSession session = ApiSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("check"), N(1 | 512 | 536870912), N(2)], default));
        Assert.Equal(1, notifications);
        Assert.Equal((nint)0, Native.SendMessageW(button, 0xf0, 0, 0));
    }

    private static IWindowsAutomationSession ApiSession() => new WindowsAutomation(
        _ => throw new InvalidOperationException("物理入力は禁止です。"),
        listSelectionPlatform: new OwnedListSelectionPlatform(),
        buttonAccessibleFactory: _ => throw new InvalidOperationException("ACCの作成は禁止です。"))
        .CreateSession();
}
