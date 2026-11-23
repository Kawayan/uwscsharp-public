using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class MouseOriginTests(ITestOutputHelper diagnostics)
{
    private sealed class CaptureBackend : IImageCapture, IWindowImageCapture
    {
        public ImageArea Desktop => new(-500, -500, 1500, 1500);

        internal readonly List<(nint Window, ImageArea Area)> Calls = [];
        internal Action? BeforeReturn;

        public PixelImage CaptureWindow(nint window, ImageArea area,
            CancellationToken token) => Read(window, area);

        public PixelImage Capture(ImageArea area, CancellationToken token) =>
            Read(0, area);

        private PixelImage Read(nint window, ImageArea area)
        {
            Calls.Add((window, area));
            int color = window == 0 ? 0xabcdef : 0x123456;
            var result = new PixelImage(area.Width, area.Height,
                [.. Enumerable.Repeat(color, area.Width * area.Height)]);
            BeforeReturn?.Invoke();
            return result;
        }
    }

    private static Value N(double value) => Value.Numeric(value);

    [Fact]
    public async Task ForegroundCaptureAndSearchStayInsideTheSelectedClient()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(1), N(1)], default);
        Assert.Equal(N(-1), await session.InvokeAsync(BuiltinFunctionNames.PeekColor,
            [N(-1), N(0)], default));
        Assert.Empty(capture.Calls);
        string path = Path.Combine(Path.GetTempPath(), "uwsc-fore-" + Guid.NewGuid() + ".bmp");
        try
        {
            new PixelImage(2, 2, [0xabcdef, 0xabcdef, 0xabcdef, 0xabcdef])
                .SaveBmp(path, default);
            Assert.Equal(N(1), await session.InvokeAsync(BuiltinFunctionNames.ChkImg,
                [Value.String(path)], default));
            Assert.Equal((0, 0), (session.ImageResult.X, session.ImageResult.Y));
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetClientRect(fixture.Root, out Native.Rect client));
            var point = new Native.Point(0, 0);
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
            Assert.Equal((0, new ImageArea(point.X, point.Y, client.Right, client.Bottom)),
                Assert.Single(capture.Calls));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task ClientCaptureUsesPhysicalCoordinatesAndRestoresDpiContext(int context)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, dpiAwarenessContext: -4);
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(1), N(2)], default);
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect outer));
            var point = new Native.Point(10, 12);
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(context));
            nint before = GetThreadDpiAwarenessContext();
            ValueTask<Value> read = session.InvokeAsync(BuiltinFunctionNames.PeekColor,
                [N(10), N(12), N(3)], default);
            Assert.True(read.IsCompletedSuccessfully);
            Assert.Equal(N(0x123456), await read);
            Assert.Equal(before, GetThreadDpiAwarenessContext());
            Assert.Equal(new ImageArea(point.X - outer.Left, point.Y - outer.Top, 1, 1),
                capture.Calls[^1].Area);
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    [Fact]
    public async Task MinimizedEmptyClientProducesNoPixelsOrImageMatches()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(1), N(2)], default);
        Native.ShowWindow(fixture.Root, 6);
        Assert.True(Native.IsIconic(fixture.Root));
        Assert.True(Native.GetClientRect(fixture.Root, out Native.Rect client));
        Assert.True(client.Right == 0 || client.Bottom == 0);
        string path = Path.Combine(Path.GetTempPath(), "uwsc-minimized-" + Guid.NewGuid() + ".bmp");
        try
        {
            new PixelImage(2, 2, [1, 2, 3, 4]).SaveBmp(path, default);
            foreach (int mode in new[] { 1, 2 })
            {
                await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
                    [N((long)fixture.Root), N(1), N(mode)], default);
                Assert.Equal(N(-1), await session.InvokeAsync(BuiltinFunctionNames.PeekColor,
                    [N(0), N(0)], default));
                Assert.Equal(N(0), await session.InvokeAsync(BuiltinFunctionNames.ChkImg,
                    [Value.String(path)], default));
            }
            Assert.Equal(WindowsImageResult.Empty, session.ImageResult);
            Assert.Empty(capture.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CaptureModeIsSessionLocalAndInvalidChangesPreserveIt()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var capture = new CaptureBackend();
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture);
        await using IWindowsAutomationSession first = automation.CreateSession();
        await using IWindowsAutomationSession second = automation.CreateSession();
        await first.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(1), N(2)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.InvokeAsync(BuiltinFunctionNames.MouseOrg,
                [N(0), N(1), N(3)], default));
        Assert.Equal(N(0x123456), await first.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(1), N(1), N(3)], default));
        Assert.Equal(N(0xabcdef), await second.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(1), N(1), N(3)], default));
        await first.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(1), Value.Empty], default);
        Assert.Equal(N(0xabcdef), await first.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(1), N(1), N(3)], default));
    }

    [Fact]
    public async Task DirectBackendReceivesWideHandleWithoutInt32Truncation()
    {
        var backend = new DirectInputTests.Backend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), backend).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N(4294967297d), N(2)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Mmv, [N(10), N(12)], default);
        Assert.Equal(4294967297L, (long)Assert.Single(backend.Messages).Window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaptureRoutingAndResetAreIndependentOfInputMode(int origin)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture).CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [N((long)fixture.Root)], default);
        foreach (int mode in new[] { 0, 1, 2 })
        {
            await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
                [id, N(origin), N(mode)], default);
            Assert.Equal(N(mode == 2 ? 0x123456 : 0xabcdef), await session.InvokeAsync(
                BuiltinFunctionNames.PeekColor, [N(10), N(12), N(3)], default));
            Assert.Equal(mode == 2 ? fixture.Root : 0, capture.Calls[^1].Window);
        }

        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg, [id, N(origin)], default);
        Assert.Equal(N(0xabcdef), await session.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(10), N(12), N(3)], default));
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg, [N(0), N(origin), N(2)], default);
        Assert.Equal(N(0xabcdef), await session.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(10), N(12), N(3)], default));
        Assert.Equal(new ImageArea(10, 12, 1, 1), capture.Calls[^1].Area);
    }

    [Fact]
    public async Task RawHandleRoutesDirectMovementAndClientCaptureWithoutRegistration()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, dpiAwarenessContext: -4);
        var capture = new CaptureBackend();
        var backend = new DirectInputTests.Backend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            backend, capture).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [N((long)fixture.Root), N(2), N(2)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Mmv, [N(10), N(12)], default);
        Assert.Equal(fixture.Root, Assert.Single(backend.Messages).Window);
        Assert.Equal(N(0x123456), await session.InvokeAsync(
            BuiltinFunctionNames.PeekColor, [N(10), N(12), N(3)], default));
        Assert.Equal(fixture.Root, capture.Calls[^1].Window);
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect outer));
        var point = new Native.Point(10, 12);
        Assert.True(Native.ClientToScreen(fixture.Root, ref point));
        Assert.Equal(new ImageArea(point.X - outer.Left, point.Y - outer.Top, 1, 1),
            capture.Calls[^1].Area);
    }

    [Fact]
    public async Task BackgroundSearchClipsToClientAndPreservesResultsOnFailure()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            imageCapture: capture).CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [N((long)fixture.Root)], default);
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg, [id, N(1), N(2)], default);
        string path = Path.Combine(Path.GetTempPath(), "uwsc-mouseorg-" + Guid.NewGuid() + ".bmp");
        try
        {
            new PixelImage(2, 2, [0x123456, 0x123456, 0x123456, 0x123456])
                .SaveBmp(path, default);
            Assert.Equal(N(1), await session.InvokeAsync(BuiltinFunctionNames.ChkImg,
                [Value.String(path), N(0), N(10), N(12), N(11), N(13)], default));
            Assert.Equal((10, 12), (session.ImageResult.X, session.ImageResult.Y));
            Assert.Equal(N(-1), await session.InvokeAsync(BuiltinFunctionNames.PeekColor,
                [N(-1), N(0)], default));
            WindowsImageResult snapshot = session.ImageResult;
            using var cancellation = new CancellationTokenSource();
            capture.BeforeReturn = cancellation.Cancel;
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.ChkImg,
                    [Value.String(path)], cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Same(snapshot, session.ImageResult);
            capture.BeforeReturn = null;
            await session.InvokeAsync(BuiltinFunctionNames.MouseOrg, [N(999), N(1), N(2)], default);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.ChkImg, [Value.String(path)], default));
            Assert.Same(snapshot, session.ImageResult);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBackgroundCaptureReleasesGdiObjectsAfterFailureAndCancellation(bool injectLeak)
    {
        var start = new ProcessStartInfo(FixturePaths.Get(
            "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--gdi-capture-probe");
        if (injectLeak)
        {
            start.ArgumentList.Add("leak");
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("GDI検証プロセスを起動できません。");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
        string text = await output;
        diagnostics.WriteLine(text);
        diagnostics.WriteLine(await errors);
        Assert.Equal(injectLeak ? 1 : 0, process.ExitCode);
        using var result = JsonDocument.Parse(text);
        JsonElement data = result.RootElement;
        Assert.NotEqual(Environment.ProcessId, data.GetProperty("pid").GetInt32());
        uint before = data.GetProperty("before").GetUInt32();
        uint after = data.GetProperty("after").GetUInt32();
        Assert.Equal(before + (injectLeak ? 1u : 0u), after);
        Assert.Equal(!injectLeak, data.GetProperty("passed").GetBoolean());
        Assert.Equal(20, data.GetProperty("failures").GetInt32());
        Assert.Equal(20, data.GetProperty("exceptions").GetInt32());
        Assert.Equal(1, data.GetProperty("cancellations").GetInt32());
    }
}
