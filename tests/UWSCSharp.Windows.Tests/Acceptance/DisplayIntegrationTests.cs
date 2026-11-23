using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class DisplayIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public void PhysicalQueriesRestoreTheCallersDpiContext(int awareness)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(awareness);
        Assert.NotEqual(0, previous);
        try
        {
            nint expected = GetThreadDpiAwarenessContext();
            var platform = new NativeDisplayPlatform();
            IReadOnlyList<DisplayMonitor> monitors = platform.Enumerate(default);
            Assert.NotEmpty(monitors);
            Assert.NotEqual(0, AreDpiAwarenessContextsEqual(
                expected, GetThreadDpiAwarenessContext()));
            _ = platform.CursorPosition();
            Assert.NotEqual(0, AreDpiAwarenessContextsEqual(
                expected, GetThreadDpiAwarenessContext()));
            Assert.True(platform.BitsPerPixel() > 0);
            Assert.NotEqual(0, AreDpiAwarenessContextsEqual(
                expected, GetThreadDpiAwarenessContext()));
            using var dpi = new PhysicalDpiScope();
            DisplayMonitor primary = Assert.Single(monitors, m => m.Primary);
            Assert.Equal(Native.GetSystemMetrics(0), primary.Width);
            Assert.Equal(Native.GetSystemMetrics(1), primary.Height);
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    public async Task MouseVariablesStayInPhysicalScreenCoordinatesWithMouseOrg(int awareness)
    {
        Assert.True(GetPhysicalCursorPos(out Native.Point previous));
        nint window = Native.CreateWindowExW(0, "STATIC", "display-owned",
            0, 123, 87, 200, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using IWindowsAutomationSession session =
                new WindowsAutomation().CreateSession();
            AssertPosition(session, 37, 61, awareness);
            await session.InvokeAsync("MOUSEORG",
                [Value.Numeric((long)window), Value.Numeric(1)], default);
            AssertPosition(session, 37, 61, awareness, false);
            AssertPosition(session, 73, 29, awareness);
        }
        finally
        {
            Native.DestroyWindow(window);
            SetPhysicalCursorPos(previous.X, previous.Y);
        }
    }

    private void AssertPosition(IWindowsAutomationSession session,
        int x, int y, int awareness, bool setPosition = true)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(awareness);
        Assert.NotEqual(0, previous);
        string conditionsBefore = ReadCursorConditions();
        long[] stamps = new long[7];
        string stage = "before-set";
        try
        {
            stamps[0] = Stopwatch.GetTimestamp();
            if (setPosition)
            {
                Assert.True(SetPhysicalCursorPos(x, y));
            }

            stamps[1] = Stopwatch.GetTimestamp();
            stage = "before-independent-read";
            Assert.True(GetPhysicalCursorPos(out Native.Point physical));
            stamps[2] = Stopwatch.GetTimestamp();
            Assert.Equal(x, physical.X);
            Assert.Equal(y, physical.Y);
            nint expectedContext = GetThreadDpiAwarenessContext();
            stage = "before-variable-x";
            Value mouseX = session.ReadDisplayVariable(WindowsDisplayVariable.MouseX, default);
            stamps[3] = Stopwatch.GetTimestamp();
            stage = "before-variable-y";
            Value mouseY = session.ReadDisplayVariable(WindowsDisplayVariable.MouseY, default);
            stamps[4] = Stopwatch.GetTimestamp();
            stage = "before-after-read";
            Assert.True(GetPhysicalCursorPos(out Native.Point after));
            stamps[5] = Stopwatch.GetTimestamp();
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point nativeAfter));
            stamps[6] = Stopwatch.GetTimestamp();
            stage = "assert-variables";
            output.WriteLine($"awareness={awareness},set={setPosition},expected={x},{y},"
                + $"before={physical.X},{physical.Y},variables={mouseX.Number},{mouseY.Number},"
                + $"after={after.X},{after.Y},native-after={nativeAfter.X},{nativeAfter.Y},"
                + $"thread={Environment.CurrentManagedThreadId},tick={Environment.TickCount64}");
            output.WriteLine(ReadCursorHistory(nativeAfter));
            Assert.Equal(x, mouseX.Number);
            Assert.Equal(y, mouseY.Number);
            Assert.NotEqual(0, AreDpiAwarenessContextsEqual(
                expectedContext, GetThreadDpiAwarenessContext()));
        }
        finally
        {
            try
            {
                output.WriteLine($"cursor-conditions awareness={awareness},set={setPosition},stage={stage},"
                    + $"qpc-frequency={Stopwatch.Frequency},phase-ticks={string.Join(',', stamps)},"
                    + $"before=[{conditionsBefore}],after=[{ReadCursorConditions()}]");
            }
            finally
            {
                Native.SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    // 取得区間を終えてから履歴を読み、失敗した取得を再試行しません。
    private static string ReadCursorHistory(Native.Point position)
    {
        var anchor = new MouseMovePoint
        {
            X = position.X & 0xffff,
            Y = position.Y & 0xffff
        };
        var points = new MouseMovePoint[8];
        int count = GetMouseMovePointsEx(
            Marshal.SizeOf<MouseMovePoint>(), ref anchor,
            points, points.Length, 1);
        if (count < 0)
        {
            return $"cursor-history count={count},error={Marshal.GetLastWin32Error()}";
        }

        string entries = string.Join(';', points.Take(count).Select(point =>
            $"{unchecked((short)point.X)},{unchecked((short)point.Y)}"
            + $"@{point.Time},extra={point.ExtraInfo}"));
        return $"cursor-history count={count},newest-first=[{entries}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseMovePoint
    {
        internal int X;
        internal int Y;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMouseMovePointsEx(
        int size, ref MouseMovePoint point,
        [Out] MouseMovePoint[] points, int count, uint resolution);

    // 座標の独立取得と変数取得の間には、入力時刻や制限範囲の追加照会を入れません。
    private static string ReadCursorConditions()
    {
        var input = new LastInputInfo
        {
            Size = checked((uint)Marshal.SizeOf<LastInputInfo>())
        };
        bool inputKnown = GetLastInputInfo(ref input);
        bool clipKnown = GetClipCursor(out Native.Rect clip);
        int clipError = clipKnown ? 0 : Marshal.GetLastWin32Error();
        return $"input-known={inputKnown},input-tick={input.Time},clip-known={clipKnown},"
            + $"clip={clip.Left},{clip.Top},{clip.Right},{clip.Bottom},clip-error={clipError}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo information);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out Native.Rect rectangle);

    [Fact]
    public async Task ScriptMonitorAndScreenVariablesAgree()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            PRINT MONITOR()>0
            PRINT MONITOR(0,MON_X)=0
            PRINT MONITOR(0,MON_Y)=0
            PRINT MONITOR(0,MON_WIDTH)=G_SCREEN_W
            PRINT MONITOR(0,MON_HEIGHT)=G_SCREEN_H
            PRINT LENGTH(MONITOR(0,MON_NAME))>0
            PRINT G_SCREEN_C>0
            """, output.Add);
        Assert.Equal(Enumerable.Repeat("1", 7), output);
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out Native.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetPhysicalCursorPos(int x, int y);
}
