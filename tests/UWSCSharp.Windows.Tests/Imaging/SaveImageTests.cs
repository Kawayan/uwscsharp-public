using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class SaveImageTests(ITestOutputHelper output)
{
    private sealed class CaptureBackend : IImageCapture, IWindowImageCapture
    {
        internal int DesktopReads;
        internal Action? AfterMetadata;
        internal Action? AfterCapture;
        internal readonly List<(nint Window, ImageArea Area)> Calls = [];

        public ImageArea Desktop
        {
            get
            {
                DesktopReads++;
                AfterMetadata?.Invoke();
                return new(-1000, -1000, 3000, 3000);
            }
        }

        public PixelImage CaptureWindow(nint window, ImageArea area,
            CancellationToken token) => Read(window, area);

        public PixelImage Capture(ImageArea area, CancellationToken token) =>
            Read(0, area);

        private PixelImage Read(nint window, ImageArea area)
        {
            Calls.Add((window, area));
            AfterCapture?.Invoke();
            return new(area.Width, area.Height,
                [.. Enumerable.Repeat(0x123456, area.Width * area.Height)]);
        }
    }

    private static Value N(long value) => Value.Numeric(value);

    private static IWindowsAutomationSession Session(CaptureBackend capture) =>
        new WindowsAutomation(_ => throw new InvalidOperationException(
            "物理入力は禁止です。"), imageCapture: capture).CreateSession();

    private static Value[] Arguments(string path, Value id, int mode) =>
        [Value.String(path), id, N(10), N(12), N(2), N(2), N(1), N(0), N(mode)];

    [Fact]
    public async Task BackgroundSupportsOffscreenWindowsWithoutReadingDesktop()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            item => Assert.True(Native.SetWindowPos(item.Root, 0,
                -800, -600, 400, 300, 0x14)), dpiAwarenessContext: -4);
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = Session(capture);
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [N((long)fixture.Root)], default);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bmp");
        try
        {
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 2), default);
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect outer));
            var point = new Native.Point(10, 12);
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
            Assert.Equal((fixture.Root, new ImageArea(point.X - outer.Left,
                point.Y - outer.Top, 2, 2)), Assert.Single(capture.Calls));
            Assert.Equal(0, capture.DesktopReads);
            Assert.Equal([0x123456, 0x123456, 0x123456, 0x123456],
                PixelImage.LoadBmp(path, default).Pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AutomaticModeUsesOnlyOcclusionInsideTheRequestedArea()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            item => Assert.True(Native.SetWindowPos(item.Root, -1,
                100, 100, 400, 300, 0x50)), title: "SAVEIMG target",
            dpiAwarenessContext: -4);
        await using PumpedControlFixture overlay = await PumpedControlFixture.CreateAsync(
            item => Assert.True(Native.SetWindowPos(item.Root, -1,
                400, 300, 50, 50, 0x50)), title: "SAVEIMG overlay",
            dpiAwarenessContext: -4);
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = Session(capture);
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [N((long)fixture.Root)], default);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bmp");
        try
        {
            // 両方の作成完了後に、取得時の位置と前後関係を所有スレッドで確定する。
            fixture.Invoke(() => Assert.True(Native.SetWindowPos(fixture.Root, -1,
                100, 100, 400, 300, 0x50)));
            overlay.Invoke(() => Assert.True(Native.SetWindowPos(overlay.Root, -1,
                400, 300, 50, 50, 0x50)));
            LogSaveArea("outside", fixture.Root, overlay.Root);
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 0), default);
            Assert.Equal(0, capture.Calls[^1].Window);
            using var dpi = new PhysicalDpiScope();
            var point = new Native.Point(10, 12);
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
            overlay.Invoke(() => Assert.True(Native.SetWindowPos(overlay.Root, -1,
                point.X - 2, point.Y - 2, 50, 50, 0x50)));
            LogSaveArea("covered", fixture.Root, overlay.Root);
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 0), default);
            Assert.Equal(fixture.Root, capture.Calls[^1].Window);
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 1), default);
            Assert.Equal(0, capture.Calls[^1].Window);
            overlay.Invoke(() => Native.ShowWindow(overlay.Root, 0));
            fixture.Invoke(() => Assert.True(Native.SetWindowPos(fixture.Root, -1,
                100, 100, 400, 300, 0x50)));
            LogSaveArea("overlay-hidden", fixture.Root, overlay.Root);
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 0), default);
            Assert.Equal(0, capture.Calls[^1].Window);
            fixture.Invoke(() => Native.ShowWindow(fixture.Root, 0));
            LogSaveArea("target-hidden", fixture.Root, overlay.Root);
            await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 0), default);
            Assert.Equal(fixture.Root, capture.Calls[^1].Window);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void LogSaveArea(string phase, nint target, nint overlay)
    {
        using var dpi = new PhysicalDpiScope();
        var point = new Native.Point(10, 12);
        Assert.True(Native.ClientToScreen(target, ref point));
        var seen = new HashSet<nint>();
        var intersections = new List<object>();
        for (nint above = Native.GetWindow(target, 3);
            above != 0 && seen.Count < 64 && seen.Add(above);
            above = Native.GetWindow(above, 3))
        {
            if (Native.IsWindowVisible(above) && !Native.IsIconic(above)
                && Native.GetWindowRect(above, out Native.Rect rectangle)
                && rectangle.Left < point.X + 2 && rectangle.Right > point.X
                && rectangle.Top < point.Y + 2 && rectangle.Bottom > point.Y)
            {
                intersections.Add(Describe(above));
            }
        }

        output.WriteLine(JsonSerializer.Serialize(new
        {
            phase,
            target = Describe(target),
            overlay = Describe(overlay),
            x = point.X,
            y = point.Y,
            width = 2,
            height = 2,
            intersections
        }));

        static object Describe(nint window)
        {
            bool hasRectangle = Native.GetWindowRect(window, out Native.Rect rectangle);
            _ = Native.GetWindowThreadProcessId(window, out uint processId);
            var title = new StringBuilder(256);
            _ = Native.GetWindowTextW(window, title, title.Capacity);
            return new
            {
                hwnd = (long)window,
                pid = processId,
                title = title.ToString(),
                visible = Native.IsWindowVisible(window),
                iconic = Native.IsIconic(window),
                has_rectangle = hasRectangle,
                left = rectangle.Left,
                top = rectangle.Top,
                right = rectangle.Right,
                bottom = rectangle.Bottom
            };
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task CoordinatesArePhysicalAndCallerDpiIsRestored(int context)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, dpiAwarenessContext: -4);
        var capture = new CaptureBackend();
        await using IWindowsAutomationSession session = Session(capture);
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [N((long)fixture.Root)], default);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bmp");
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect outer));
            var point = new Native.Point(10, 12);
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(context));
            nint before = GetThreadDpiAwarenessContext();
            ValueTask<Value> operation = session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                Arguments(path, id, 2), default);
            Assert.True(operation.IsCompletedSuccessfully);
            await operation;
            Assert.Equal(before, GetThreadDpiAwarenessContext());
            Assert.Equal(new ImageArea(point.X - outer.Left,
                point.Y - outer.Top, 2, 2), Assert.Single(capture.Calls).Area);
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterNativeBoundaryPreservesTheDestination(bool afterCapture)
    {
        var capture = new CaptureBackend();
        using var cancellation = new CancellationTokenSource();
        if (afterCapture)
        {
            capture.AfterCapture = cancellation.Cancel;
        }
        else
        {
            capture.AfterMetadata = cancellation.Cancel;
        }

        await using IWindowsAutomationSession session = Session(capture);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bmp");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await session.InvokeAsync(BuiltinFunctionNames.SaveImg,
                    [Value.String(path), N(0), N(0), N(0), N(2), N(2)], cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
            Assert.Equal(afterCapture ? 1 : 0, capture.Calls.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
