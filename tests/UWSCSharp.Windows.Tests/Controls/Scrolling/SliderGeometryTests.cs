using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class SliderGeometryTests
{
    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public void PhysicalScopeRestoresCallerContextAfterFailure(int context)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(context);
        Assert.NotEqual(0, previous);
        try
        {
            nint before = GetThreadDpiAwarenessContext();
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var dpi = new PhysicalDpiScope();
                Assert.True(AreDpiAwarenessContextsEqual(
                    -4, GetThreadDpiAwarenessContext()));
                throw new InvalidOperationException("復元確認用の失敗です。");
            }));
            Assert.True(AreDpiAwarenessContextsEqual(
                before, GetThreadDpiAwarenessContext()));
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task PublicGeometryUsesPhysicalClientCoordinates(int context)
    {
        SliderControlTests.InitializeControls();
        nint slider = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            owner => slider = owner.Child("msctls_trackbar32", "", 0, 23),
            dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(slider, out Native.Rect outer));
            var first = new Native.Point(outer.Left, outer.Top);
            var last = new Native.Point(outer.Right, outer.Bottom);
            Assert.True(Native.ScreenToClient(fixture.Root, ref first));
            Assert.True(Native.ScreenToClient(fixture.Root, ref last));
            int[] expected = [first.X, first.Y, last.X, last.Y];
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(context));
            nint before = GetThreadDpiAwarenessContext();
            for (int item = 5; item <= 8; item++)
            {
                ValueTask<Value> read = session.InvokeAsync(BuiltinFunctionNames.GetSlider,
                    [id, Value.Numeric(1), Value.Numeric(item)], default);
                Assert.True(read.IsCompletedSuccessfully);
                Assert.Equal(Value.Numeric(expected[item - 5]), await read);
                Assert.True(AreDpiAwarenessContextsEqual(
                    before, GetThreadDpiAwarenessContext()));
            }
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }
}
