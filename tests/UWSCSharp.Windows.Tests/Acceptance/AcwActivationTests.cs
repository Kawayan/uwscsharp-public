using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class AcwActivationTests
{
    [Theory]
    [InlineData("hidden")]
    [InlineData("normal")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task GeometryOmissionRequestsForegroundWithoutRestoringState(string state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.Rect normal = Bounds(fixture.Root);
        SetState(fixture.Root, state);
        var backend = new Activation();
        await using IWindowsAutomationSession session = Session(backend);
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(BuiltinFunctionNames.Acw, [id], default));
        Assert.Equal(["foreground"], backend.Calls);
        Assert.Equal([fixture.Root], backend.Targets);
        Assert.Equal(state != "hidden", Native.IsWindowVisible(fixture.Root));
        Assert.Equal(state == "minimized", Native.IsIconic(fixture.Root));
        Assert.Equal(state == "maximized", Native.IsZoomed(fixture.Root));
        Native.ShowWindow(fixture.Root, 4);
        Assert.Equal(normal, Bounds(fixture.Root));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task MoveRestoresBeforeForegroundAndPreservesNormalDimensions(string state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.Rect normal = Bounds(fixture.Root);
        SetState(fixture.Root, state);
        var backend = new Activation();
        await using IWindowsAutomationSession session = Session(backend);
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [id, Value.Numeric(40), Value.Numeric(60)], default));
        Assert.Equal(["normal", "foreground"], backend.Calls);
        Assert.Equal([fixture.Root, fixture.Root], backend.Targets);
        Assert.True(Native.IsWindowVisible(fixture.Root));
        Assert.False(Native.IsIconic(fixture.Root));
        Assert.False(Native.IsZoomed(fixture.Root));
        Native.Rect after = Bounds(fixture.Root);
        Assert.Equal((40, 60, normal.Right - normal.Left, normal.Bottom - normal.Top),
            (after.Left, after.Top, after.Right - after.Left, after.Bottom - after.Top));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task ResizeOnlyPreservesTheRestoredNormalPosition(string state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.Rect normal = Bounds(fixture.Root);
        SetState(fixture.Root, state);
        var backend = new Activation();
        await using IWindowsAutomationSession session = Session(backend);
        Value id = await Register(session, fixture.Root);
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [id, new(ValueKind.Omitted), new(ValueKind.Omitted), Value.Numeric(400)], default);
        Native.Rect after = Bounds(fixture.Root);
        Assert.Equal(["normal", "foreground"], backend.Calls);
        Assert.Equal((normal.Left, normal.Top, 400, normal.Bottom - normal.Top),
            (after.Left, after.Top, after.Right - after.Left, after.Bottom - after.Top));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task ExplicitUnchangedBoundsDoNotRestoreState(string state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        SetState(fixture.Root, state);
        Native.Rect before = Bounds(fixture.Root);
        var backend = new Activation();
        await using IWindowsAutomationSession session = Session(backend);
        Value id = await Register(session, fixture.Root);
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [id, Value.Numeric(before.Left), Value.Numeric(before.Top),
                Value.Numeric(before.Right - before.Left),
                Value.Numeric(before.Bottom - before.Top)], default);
        Assert.Equal(["foreground"], backend.Calls);
        Assert.Equal(state != "hidden", Native.IsWindowVisible(fixture.Root));
        Assert.Equal(state == "minimized", Native.IsIconic(fixture.Root));
        Assert.Equal(state == "maximized", Native.IsZoomed(fixture.Root));
        Assert.Equal(before, Bounds(fixture.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBetweenStagesStopsBeforePlacement(bool afterForeground)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.Rect normal = Bounds(fixture.Root);
        SetState(fixture.Root, "minimized");
        using var cancellation = new CancellationTokenSource();
        var backend = new Activation();
        if (afterForeground)
        {
            backend.OnForeground = cancellation.Cancel;
        }
        else
        {
            backend.OnNormal = cancellation.Cancel;
        }

        await using IWindowsAutomationSession session = Session(backend);
        Value id = await Register(session, fixture.Root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Acw,
                [id, Value.Numeric(40), Value.Numeric(60)], cancellation.Token));
        Assert.Equal(afterForeground ? ["normal", "foreground"] : ["normal"], backend.Calls);
        Assert.False(Native.IsIconic(fixture.Root));
        Assert.Equal(normal, Bounds(fixture.Root));
    }

    [Fact]
    public async Task InvalidTargetMakesNoActivationRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Activation();
        await using IWindowsAutomationSession session = Session(backend);
        Assert.Equal(Value.Empty, await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(-1), Value.Numeric(40), Value.Numeric(60)], default));
        Assert.Empty(backend.Calls);
    }

    private static Task<PumpedControlFixture> Fixture() => PumpedControlFixture.CreateAsync(
        _ => { }, title: "ACW-activation-" + Guid.NewGuid(), dpiAwarenessContext: -4);

    private static IWindowsAutomationSession Session(IWindowActivation backend) =>
        new WindowsAutomation(_ => Assert.Fail("ACWは入力を合成しません。"),
            windowActivationBackend: backend).CreateSession();

    private static ValueTask<Value> Register(IWindowsAutomationSession session, nint window) =>
        session.InvokeAsync(BuiltinFunctionNames.HndToId, [Value.Numeric((long)window)], default);

    private static void SetState(nint window, string state) => Native.ShowWindow(window,
        state switch
        {
            "hidden" => 0,
            "minimized" => 7,
            "maximized" => 3,
            _ => 4
        });

    private static Native.Rect Bounds(nint window)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            return rect;
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    private sealed class Activation : IWindowActivation
    {
        internal List<string> Calls
        {
            get;
        } = [];

        internal List<nint> Targets
        {
            get;
        } = [];

        internal Action? OnNormal
        {
            get;
            set;
        }

        internal Action? OnForeground
        {
            get;
            set;
        }

        /// <summary>
        /// 所有ウィンドウを通常表示へ戻し、取消条件を通知します。
        /// </summary>
        public void ShowNormal(nint window)
        {
            Calls.Add("normal");
            Targets.Add(window);
            Native.ShowWindow(window, 1);
            OnNormal?.Invoke();
        }

        /// <summary>
        /// 前面化の拒否を返し、配置が継続するか確認します。
        /// </summary>
        public bool RequestForeground(nint window)
        {
            Calls.Add("foreground");
            Targets.Add(window);
            OnForeground?.Invoke();
            return false;
        }
    }
}
