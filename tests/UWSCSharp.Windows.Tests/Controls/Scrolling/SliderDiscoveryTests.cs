using System.ComponentModel;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SliderDiscoveryTests
{
    [Theory]
    [InlineData("msctls_trackbar32", 1)]
    [InlineData("WindowsForms10.msctls_trackbar32.app.owned", 1)]
    [InlineData("TTrackBar", 1)]
    [InlineData("ScrollBar", 2)]
    [InlineData("WindowsForms10.SCROLLBAR.app.owned", 2)]
    [InlineData("TScrollBar", 2)]
    [InlineData("STATIC", 0)]
    public void ClassifiesProtocolNamesAndAliases(string name, int kind)
    {
        Assert.Equal((NativeSliderKind)kind, NativeSliderDiscovery.Kind(name));
    }

    [Fact]
    public void CancellationStopsInsideTheCallbackAndRetainsItsToken()
    {
        using var cancellation = new CancellationTokenSource();
        List<nint> visited = [];
        int callbacks = 0;
        bool stopped = false;
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeSliderDiscovery.VisitChildren(1, child =>
            {
                visited.Add(child);
                if (visited.Count == 3)
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token, callback =>
            {
                for (int index = 1; index <= 20; index++)
                {
                    callbacks++;
                    if (!callback(index, 0))
                    {
                        stopped = true;
                        break;
                    }
                }

                return !stopped;
            }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(3, callbacks);
        Assert.Equal([(nint)1, (nint)2, (nint)3], visited);
        Assert.True(stopped);
    }

    [Fact]
    public void CallbackFailureIsRethrownOnlyAfterNativeEnumerationReturns()
    {
        var expected = new IOException("所有候補の取得失敗");
        bool returnedFromCallback = false;
        IOException actual = Assert.Throws<IOException>(() =>
            NativeSliderDiscovery.VisitChildren(1, _ => throw expected,
                default, callback =>
                {
                    bool continued = callback(2, 0);
                    returnedFromCallback = true;
                    return continued;
                }));
        Assert.Same(expected, actual);
        Assert.True(returnedFromCallback);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task LegacyQueryDoesNotChangePositionOrNotify(int number)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls();
        nint target = 0;
        int notifications = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            target = owner.Child("msctls_trackbar32", "", 0, 33);
            Native.SendMessageW(target, Native.TbmSetPos, 1, 37);
            owner.Scroll = (_, _, _) => notifications++;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Numeric(37), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [id, ExpressionEvaluator.Evaluate("GET_SLIDER"),
                Value.Numeric(number)], default));
        Assert.Equal(Value.Numeric(37), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id, Value.Numeric(number)], default));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task NegativeOccurrenceIncludesDisabledControlsAndAvoidsAbsOverflow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls();
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            nint first = owner.Child("msctls_trackbar32", "", 0x08000000, 10);
            nint second = owner.Child("msctls_trackbar32", "", 0, 80);
            Native.SendMessageW(first, Native.TbmSetPos, 1, 23);
            Native.SendMessageW(second, Native.TbmSetPos, 1, 67);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Numeric(67), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider, [id, Value.Numeric(-10000000)], default));
        Assert.Equal(Value.Numeric(23), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [id, Value.Numeric(-10000000), Value.Numeric(-1)], default));
        Assert.Equal(Value.Numeric(67), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id, Value.Numeric(-2)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [id, Value.Numeric(50), Value.Numeric(-1)], default));
        Assert.Equal(Value.Numeric(50), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id, Value.Numeric(-1)], default));
        Assert.Equal(Value.Numeric(-999999), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [id, Value.Numeric(-10000000), Value.Numeric(int.MinValue)], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [id, Value.Numeric(50), Value.Numeric(int.MinValue)], default));
    }

    [Fact]
    public async Task ExplicitZeroUsesTheControlledForegroundAndEmptyIdIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls();
        nint target = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            target = owner.Child("msctls_trackbar32", "", 0, 33);
            Native.SendMessageW(target, Native.TbmSetPos, 1, 37);
        }, dpiAwarenessContext: -4);
        var environment = new Foreground(fixture.Root);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("OS入力の合成は禁止です。"),
            windowLookup: environment).CreateSession();
        Assert.Equal(Value.Numeric(37), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [Value.Numeric(0), Value.Numeric(-10000000)], default));
        Assert.Equal(Value.Numeric(37), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [Value.Numeric(0)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.SetSlider,
            [Value.Numeric(0), Value.Numeric(50)], default));
        Assert.Equal(Value.Numeric(50), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [Value.Numeric(0)], default));
        Native.Point point;
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(target, out Native.Rect bounds));
            point = new(bounds.Left, bounds.Top);
            Assert.True(Native.ScreenToClient(fixture.Root, ref point));
        }
        Assert.Equal(Value.Numeric(point.X), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider,
            [Value.Numeric(0), Value.Numeric(1), Value.Numeric(5)], default));
        Assert.Equal(Value.Numeric(point.Y), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider,
            [Value.Numeric(0), Value.Numeric(1), Value.Numeric(6)], default));
        foreach (string name in new[] { BuiltinFunctionNames.SetSlider,
            BuiltinFunctionNames.GetSlider })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(name,
                    [Value.Empty, Value.Numeric(1)], default));
        }
    }

    [Fact]
    public async Task UnresponsiveRangeReadTimesOutBeforeChangingPosition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls();
        nint target = 0;
        int notifications = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            target = owner.Child("msctls_trackbar32", "", 0, 20);
            Native.SendMessageW(target, Native.TbmSetPos, 1, 37);
            owner.Scroll = (_, _, _) => notifications++;
        });
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var hook = new ToolbarClickTextTests.Interceptor(target,
            (message, _, _) =>
            {
                if (message == Native.TbmGetRangeMin)
                {
                    Thread.Sleep(1500);
                }

                return null;
            }, after: (message, _, _) =>
            {
                if (message == Native.TbmGetRangeMin)
                {
                    completed.TrySetResult();
                }
            });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        try
        {
            Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.SetSlider,
                    [id, Value.Numeric(50)], default));
            Assert.True(WindowsOperationErrors.Contains(error));
        }
        finally
        {
            // callbackの終了を待ってから元のウィンドウ手続きへ戻します。
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(Value.Numeric(37), await session.InvokeAsync(
            BuiltinFunctionNames.GetSlider, [id], default));
        Assert.Equal(0, notifications);
    }

    private static ValueTask<Value> Register(
        IWindowsAutomationSession session, nint window) =>
        session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)window)], default);

    private sealed class Foreground(nint root) : IWindowLookupEnvironment
    {
        public nint ForegroundWindow() => root;

        public Native.Point CursorPosition() =>
            throw new InvalidOperationException("カーソルは取得しません。");

        public nint WindowAt(Native.Point point) =>
            throw new InvalidOperationException("画面のヒット判定は行いません。");

        public nint RootOf(nint window) =>
            throw new InvalidOperationException("親の探索は行いません。");
    }
}
