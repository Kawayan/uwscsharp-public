using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeGetStrTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "  日本語\t...  ")]
    [InlineData(1, "")]
    [InlineData(1, "&Label...")]
    public async Task NativeTextPreservesEmptyAndUntrimmedValues(int kind, string text)
    {
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                owner.Child(kind == 0 ? "EDIT" : "STATIC", text, 0, 20));
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Value value = await session.InvokeAsync(BuiltinFunctionNames.GetStr,
            [id, Value.Numeric(1), Value.Numeric(kind)], default);
        Assert.Equal(ValueKind.String, value.Kind);
        Assert.Equal(text, value.Text);
    }

    [Fact]
    public async Task StatusPartBoundsBelongToTheirOwnClientAreas()
    {
        nint status = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                status = StatusControlTests.Create(owner, 20, 0, "left", "right"),
                dpiAwarenessContext: -4);
        Native.Rect first = await NativeStatusControl.ReadPartBoundsAsync(status, 1, default);
        Native.Rect second = await NativeStatusControl.ReadPartBoundsAsync(status, 2, default);
        Assert.True(first.Left < first.Right);
        Assert.True(first.Top < first.Bottom);
        Assert.True(second.Left >= first.Right);
        Assert.True(second.Left < second.Right);
        Assert.Equal(first.Top, second.Top);
        Assert.Equal(first.Bottom, second.Bottom);
        using var dpi = new PhysicalDpiScope();
        var origin = new Native.Point(0, 0);
        Assert.True(Native.ClientToScreen(status, ref origin));
        Assert.InRange(first.Left, origin.X, origin.X + 10);
        Assert.InRange(second.Left, origin.X + 30, origin.X + 40);
    }

    [Fact]
    public async Task StatusBoundsCheckCancellationBeforeReading()
    {
        nint status = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                status = StatusControlTests.Create(owner, 20, 0, "left"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeStatusControl.ReadPartBoundsAsync(status, 1, stop.Token));
    }

    [Fact]
    public void MsaaSelectedLocationIsCapturedOnlyForTheChosenElement()
    {
        var root = new AccessibleTestProvider(10, 0, "", "",
            new AccessibleTestProvider(42, 0, "", "first"),
            new AccessibleTestProvider(42, 0, "", "second"));
        var locations = new List<Native.Rect>();
        List<string> values = AccessibleItems.ReadProvider(root, 0, false,
            true, 1, default, 3, 2, selected: locations.Add);
        Assert.Equal(["second"], values);
        Native.Rect bounds = Assert.Single(locations);
        Assert.Equal(10, bounds.Left);
        Assert.Equal(10, bounds.Top);
        Assert.Equal(110, bounds.Right);
        Assert.Equal(30, bounds.Bottom);
    }

    [Fact]
    public void CancellationDuringValueReadDoesNotPublishMovementGeometry()
    {
        using var stop = new CancellationTokenSource();
        var target = new AccessibleTestProvider(42, 0, "", "text")
        {
            ReadingValue = stop.Cancel
        };
        var locations = new List<Native.Rect>();
        Assert.Throws<OperationCanceledException>(() =>
            AccessibleItems.ReadProvider(target, 0, false, true, 1,
                stop.Token, 3, 1, selected: locations.Add));
        Assert.Empty(locations);
    }
}
