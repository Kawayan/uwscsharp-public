using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeListSelectionBackendTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CursorPreparationSavesAfterMoveAndRestoresOnlyForBack(bool back, bool move)
    {
        var platform = new Platform();
        var backend = new NativeListSelectionBackend(10, 20, true, back, platform, move);
        backend.PrepareComboCursor(default);
        backend.RestoreComboCursor();
        var expected = new List<string>();
        if (move)
        {
            expected.Add("move:10:20");
        }

        expected.Add("cursor");
        if (back)
        {
            expected.Add("move:104:40");
            expected.Add(move ? "move:10:20" : "move:7:9");
        }

        Assert.Equal(expected, platform.Calls);
        backend.RestoreComboCursor();
        Assert.Equal(expected, platform.Calls);
    }

    [Theory]
    [InlineData(89, 93)]
    [InlineData(90, -104)]
    [InlineData(91, -104)]
    public void CursorAvoidsRightEdgeUsingVirtualDesktopAndTruncatesNegativeMidpoint(int right, int x)
    {
        var platform = new Platform
        {
            ScreenLeft = -1000,
            ScreenWidth = 1100,
            Rectangle = new Native.Rect { Left = -100, Top = -4, Right = right, Bottom = 1 },
        };
        var backend = new NativeListSelectionBackend(10, 20, true, true, platform);
        backend.PrepareComboCursor(default);
        Assert.Equal($"move:{x}:-1", platform.Calls[^1]);
    }

    [Fact]
    public void CoordinateOverflowCanStillRestoreSavedCursor()
    {
        var platform = new Platform { ScreenLeft = int.MaxValue };
        var backend = new NativeListSelectionBackend(10, 20, true, true, platform);
        Assert.Throws<OverflowException>(() => backend.PrepareComboCursor(default));
        backend.RestoreComboCursor();
        Assert.Equal(["cursor", "move:7:9"], platform.Calls);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(8u, false)]
    public void ComboPreparationUsesClassStyleAndYieldsAfterShowing(uint style, bool opens)
    {
        var platform = new Platform { Style = style };
        var backend = new NativeListSelectionBackend(10, 20, true, true, platform);
        Assert.Equal(opens, backend.OpenCombo(default));
        if (opens)
        {
            Assert.Equal("send:20:335:1:0:False:0:0", platform.Calls[0]);
            Assert.Equal(20, platform.Calls.Count(call => call == "sleep:0"));
            Assert.Equal(20, platform.Calls.Count(call => call == "pump"));
            backend.CloseCombo();
            Assert.Equal("send:20:335:0:0:False:0:0", platform.Calls[^1]);
        }
        else
        {
            Assert.Empty(platform.Calls);
        }
    }

    [Fact]
    public void CancellationDuringDropdownNotificationClosesBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnSend = message => { if (message == 0x14f) { cancellation.Cancel(); } } };
        var backend = new NativeListSelectionBackend(10, 20, true, true, platform);
        Assert.ThrowsAny<OperationCanceledException>(() => backend.OpenCombo(cancellation.Token));
        Assert.Equal(["send:20:335:1:0:False:0:0", "send:20:335:0:0:False:0:0"], platform.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyMessagesRestorePriorityAndDetach(bool back)
    {
        var platform = new Platform();
        var backend = new NativeListSelectionBackend(10, 20, false, back, platform);
        backend.PressKey(40, default);
        string[] preparation = back ? [] : ["attach:True", "foreground:10", "focus:20", "attach:False"];
        Assert.Equal(preparation.Concat(["priority:256", "send:20:256:40:0:True:1000:2",
            "send:20:257:40:-1073741824:True:1000:2", "send:20:0:0:0:True:1000:2",
            "send:10:0:0:0:True:1000:2", "priority:32"]), platform.Calls);
    }

    [Fact]
    public void CancellationDuringKeyDownStillSendsKeyUpAndNormalPriority()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnSend = message => { if (message == 0x100) { cancellation.Cancel(); } } };
        var backend = new NativeListSelectionBackend(10, 20, false, true, platform);
        Assert.ThrowsAny<OperationCanceledException>(() => backend.PressKey(40, cancellation.Token));
        Assert.Contains("send:20:257:40:-1073741824:True:1000:2", platform.Calls);
        Assert.Equal("priority:32", platform.Calls[^1]);
        Assert.DoesNotContain("send:10:0:0:0:True:1000:2", platform.Calls);
    }

    [Fact]
    public void FocusFailureDetachesInputThreads()
    {
        var platform = new Platform { FailFocus = true };
        var backend = new NativeListSelectionBackend(10, 20, false, false, platform);
        Assert.Throws<InvalidOperationException>(() => backend.PressKey(40, default));
        Assert.Equal("attach:False", platform.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootPreparationUsesEightSecondProbeExceptHost(bool host)
    {
        var platform = new Platform { Host = host };
        var backend = new NativeListSelectionBackend(10, 20, false, true, platform);
        backend.PrepareRoot(default);
        Assert.Equal(host ? ["sleep:0", "pump"] : new[] { "sleep:0", "pump",
            "send:10:0:0:0:True:8000:0", "sleep:0", "pump" }, platform.Calls);
    }

    private sealed class Platform : IListSelectionPlatform
    {
        internal List<string> Calls { get; } = [];

        internal Action<uint>? OnSend
        {
            get; init;
        }

        internal bool FailFocus
        {
            get; init;
        }

        internal bool Host
        {
            get; init;
        }

        public void Activate(nint root, CancellationToken token) => Calls.Add($"activate:{root}");

        public Native.Rect Bounds(nint window) => Rectangle;

        internal Native.Rect Rectangle = new() { Left = 10, Top = 20, Right = 100, Bottom = 60 };
        internal int ScreenLeft;
        internal int ScreenWidth = 1000;
        private Native.Point cursor = new(7, 9);

        public Native.Point Cursor()
        {
            Calls.Add("cursor");
            return cursor;
        }

        public void MoveCursor(Native.Point point)
        {
            cursor = point;
            Calls.Add($"move:{point.X}:{point.Y}");
        }

        public int Metric(int index) => index == 76 ? ScreenLeft : ScreenWidth;

        public uint ClassStyle(nint window) => Style;

        public bool IsIconic(nint window) => false;

        public bool IsVisible(nint window) => true;

        public nint ForegroundWindow() => 0;

        internal uint Style { get; init; } = 3;

        public bool IsWindow(nint window) => true;

        public bool IsHostWindow(nint window) => Host;

        public uint CurrentThread() => 1;

        public uint WindowThread(nint window) => 2;

        public bool Attach(uint target, uint current, bool attach)
        {
            Assert.Equal(2u, target);
            Assert.Equal(1u, current);
            Calls.Add($"attach:{attach}");
            return true;
        }

        public void Foreground(nint window) => Calls.Add($"foreground:{window}");

        public void Focus(nint window)
        {
            Calls.Add($"focus:{window}");
            if (FailFocus)
            {
                throw new InvalidOperationException();
            }
        }

        public void Priority(uint value) => Calls.Add($"priority:{value}");

        public void LockUpdates(nint window) => Calls.Add($"lock:{window}");

        public nint Send(nint window, uint message, nint parameter, nint data,
            bool timed, uint timeout, uint flags)
        {
            Calls.Add($"send:{window}:{message}:{parameter}:{data}:{timed}:{timeout}:{flags}");
            OnSend?.Invoke(message);
            return 0;
        }

        public void Sleep(int milliseconds, CancellationToken token) => Calls.Add($"sleep:{milliseconds}");

        public void Pump(CancellationToken token) => Calls.Add("pump");
    }
}
