using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class CtrlWinContractTests
{
    private readonly ITestOutputHelper output;

    public CtrlWinContractTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task NormalDoesNotRestoreThePreviousMaximizedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.Rect normal = Bounds(fixture.Root);
        Native.ShowWindow(fixture.Root, 3);
        Native.ShowWindow(fixture.Root, 7);
        Assert.True(Native.IsIconic(fixture.Root));
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(
            BuiltinFunctionNames.CtrlWin, [id, Value.Numeric(8)], default));
        Assert.True(Native.IsWindowVisible(fixture.Root));
        Assert.False(Native.IsIconic(fixture.Root));
        Assert.False(Native.IsZoomed(fixture.Root));
        Assert.Equal(normal, Bounds(fixture.Root));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("visible")]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task TopNoActivateSetsTopmostWithoutChangingState(
        string state)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        Native.ShowWindow(fixture.Root, state switch
        {
            "hidden" => 0,
            "minimized" => 7,
            "maximized" => 3,
            _ => 8
        });
        Native.Rect before = Bounds(fixture.Root);
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(
            BuiltinFunctionNames.CtrlWin, [id, Value.Numeric(11)], default));
        int topmost = Native.GetWindowLongW(fixture.Root, -20) & 8;
        if (topmost == 0)
        {
            Trace(fixture.Root, "after-" + state);
        }

        Assert.NotEqual(0, topmost);
        Assert.Equal(state != "hidden", Native.IsWindowVisible(fixture.Root));
        Assert.Equal(state == "minimized", Native.IsIconic(fixture.Root));
        Assert.Equal(state == "maximized", Native.IsZoomed(fixture.Root));
        Assert.Equal(before, Bounds(fixture.Root));
        await session.InvokeAsync(BuiltinFunctionNames.CtrlWin,
            [id, Value.Numeric(10)], default);
        Assert.Equal(0, Native.GetWindowLongW(fixture.Root, -20) & 8);
    }

    [Fact]
    public async Task ActivationRefusalIsOneRequestWithoutAnException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await Fixture();
        var backend = new Activation();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            windowActivationBackend: backend).CreateSession();
        Value id = await Register(session, fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(
            BuiltinFunctionNames.CtrlWin, [id, Value.Numeric(1)], default));
        Assert.Equal([fixture.Root], backend.Targets);
        Assert.Equal(Value.Empty, await session.InvokeAsync(
            BuiltinFunctionNames.CtrlWin,
            [Value.Numeric(-1), Value.Numeric(1)], default));
        Assert.Single(backend.Targets);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.CtrlWin,
                [id, Value.Numeric(1)], cancellation.Token));
        Assert.Single(backend.Targets);
    }

    [Fact]
    public async Task TopNoActivatePreservesMinimizedStateAcrossRepeatedCreation()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            await TopNoActivateSetsTopmostWithoutChangingState("minimized");
        }
    }

    private static Task<PumpedControlFixture> Fixture() =>
        PumpedControlFixture.CreateAsync(_ => { },
            title: "CTRLWIN-" + Guid.NewGuid(), dpiAwarenessContext: -4);

    private void Trace(nint window, string phase)
    {
        output.WriteLine(
            "{0}: hwnd={1:X} style={2:X} ex={3:X} visible={4} iconic={5} zoomed={6} previous={7:X}",
            phase, window, Native.GetWindowLongW(window, -16),
            Native.GetWindowLongW(window, -20),
            Native.IsWindowVisible(window), Native.IsIconic(window),
            Native.IsZoomed(window), Native.GetWindow(window, 3));
    }

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

    private static ValueTask<Value> Register(
        IWindowsAutomationSession session, nint window) =>
        session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)window)], default);

    private sealed class Activation : IWindowActivation
    {
        internal List<nint> Targets
        {
            get;
        } = [];

        public void ShowNormal(nint window) =>
            throw new InvalidOperationException("表示状態は変更しません。");

        public bool RequestForeground(nint window)
        {
            Targets.Add(window);
            return false;
        }
    }
}
