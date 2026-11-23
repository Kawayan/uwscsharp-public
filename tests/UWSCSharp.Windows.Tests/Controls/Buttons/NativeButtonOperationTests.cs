using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed partial class NativeButtonOperationTests
{
    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal List<string> Events { get; } = [];
        internal List<(uint Message, nint Parameter, nint Data)> Posts { get; } = [];
        internal List<nint> PostWindows { get; } = [];
        internal Action? Posting
        {
            get; set;
        }

        public override void Activate(nint root, CancellationToken token) => Events.Add("activate");

        public override bool Attach(uint target, uint current, bool attach)
        {
            Events.Add("attach" + attach);
            return true;
        }

        public override void Foreground(nint window) => Events.Add("foreground");

        public override void Focus(nint window) => Events.Add("focus");

        public override void Priority(uint value) => Events.Add("priority" + value);

        public override void Post(nint window, uint message, nint parameter, nint data)
        {
            Posts.Add((message, parameter, data));
            PostWindows.Add(window);
            Events.Add("post" + message);
            Posting?.Invoke();
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (milliseconds == 20)
            {
                Events.Add("sleep20");
            }
        }

        public override void Pump(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add("pump");
        }

        public override Native.Rect Bounds(nint window) => new()
        {
            Left = 10,
            Top = 20,
            Right = 21,
            Bottom = 35
        };

        public override void MoveCursor(Native.Point point) =>
            Events.Add($"move{point.X},{point.Y}");
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(2, 0, 1, false)]
    [InlineData(3, 0, 1, true)]
    [InlineData(5, 2, 2, false)]
    [InlineData(6, 2, 1, true)]
    [InlineData(7, 0, 1, true)]
    [InlineData(9, 1, 0, false)]
    [InlineData(11, 0, 1, true)]
    public async Task NormalButtonReturnsMatchSuccessWithoutPostStateCheck(
        int style, int state, int request, bool back)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", style, 10);
        Native.SendMessageW(button, 0xf1, state, 0);
        var platform = new Platform();
        int reads = 0;
        int clicks = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(button, (message, _, _) =>
        {
            reads += message == 0xf0 ? 1 : 0;
            clicks += message == 0xf5 ? 1 : 0;
            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(1 | (back ? 512 : 0)),
                Value.Numeric(request)], default));
        Assert.Equal(1, reads);
        Assert.Equal(0, clicks);
        Assert.Equal(state == request ? 0 : 2, platform.Posts.Count);
        Assert.Equal(back ? 0 : 1, platform.Events.Count(value => value == "activate"));
        Assert.Equal(!back && state == request,
            platform.Events.Contains("attachTrue"));
        Assert.DoesNotContain("pump", platform.Events);
        Assert.All(platform.Posts, post =>
        {
            Assert.Equal(0, post.Parameter);
            Assert.Equal(0, post.Data);
        });
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(15360)]
    public async Task ExplicitPointerFlagsOverrideSameState(int flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        Native.SendMessageW(button, 0xf1, 1, 0);
        var platform = new Platform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(1 | 512 | flags)], default));
        Assert.Equal((flags & 1024) != 0, platform.Events.Contains("move15,27"));
        uint[] expected = flags == 1024 ? [] : (flags & 4096) != 0
            ? (flags & 8192) != 0 ? [0x204, 0x205, 0x205, 0x206, 0x205, 0x205]
            : [0x204, 0x205, 0x205]
            : (flags & 8192) != 0 ? [0x201, 0x202, 0x203, 0x202] : [0x201, 0x202];
        Assert.Equal(expected, platform.Posts.Select(post => post.Message));
        Assert.All(platform.Posts, post => Assert.Equal(0, post.Data));
        Assert.Null(fixture.CallbackFailure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassInfoExW(nint instance, string name, ref WindowClass value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [Theory]
    [InlineData("CHECKBOX", 0, 0, 1, false)]
    [InlineData("CHECKBOX", 0, 1, 1, false)]
    [InlineData("CHECKBOX", 0, 1, 2, true)]
    [InlineData("CHECKBOX", 2, 0, 1, true)]
    [InlineData("CHECKBOX", 2, 1, 1, false)]
    [InlineData("3STATE", 1, 2, 0, true)]
    [InlineData("3STATE", 1, 0, 2, true)]
    [InlineData("3STATE", 2, 2, 2, false)]
    [InlineData("CHECKBOX", 2, 0, 1, true, true)]
    [InlineData("CHECKBOX", 2, 1, 1, false, true)]
    public void ClassHintLimitsReadbackAndOneAdditionalRequest(
        string hint, int before, int after, int request, bool additional,
        bool accessibleRetry = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = hint + Guid.NewGuid().ToString("N");
        nint instance = GetModuleHandleW(null);
        nint pointer = Marshal.StringToHGlobalUni(name);
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        try
        {
            Assert.True(GetClassInfoExW(0, "STATIC", ref definition));
            definition.ClassName = pointer;
            definition.Instance = instance;
            definition.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref definition));
            try
            {
                using var fixture = new ButtonControlTests.Fixture();
                nint button = fixture.Child(name, "target", 0, 10);
                var platform = new Platform();
                using var accessible = new Accessible();
                accessible.Marker.Enqueue(false);
                accessible.Marker.Enqueue(true);
                accessible.States.Enqueue((after, 44, true));
                int reads = 0;
                using var hook = new ToolbarClickTextTests.Interceptor(button, (message, _, _) =>
                {
                    if (message != 0xf0)
                    {
                        return null;
                    }

                    reads++;
                    return reads == 1 ? before : after;
                });
                Assert.True(NativeButtonControl.Click(fixture.Root,
                    button, request, 1 | 512, platform, default,
                    accessibleRetry ? accessible : null));
                bool same = before == request;
                Assert.Equal(same || accessibleRetry ? 1 : 2, reads);
                Assert.Equal(same ? 0 : additional && !accessibleRetry ? 4 : 2, platform.Posts.Count);
                Assert.Equal(accessibleRetry && additional ? 1 : 0, accessible.Actions);
                Assert.Equal(same ? 0 : 2, platform.Events.Count(value => value == "sleep20"));
                Assert.Equal(same ? 0 : 40, platform.Events.Count(value => value == "pump"));
                Assert.Null(fixture.CallbackFailure);
            }
            finally
            {
                Assert.True(UnregisterClassW(name, instance));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostFailureStillRequestsUpAndPriorityRestore(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform();
        platform.Posting = () =>
        {
            platform.Posting = null;
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException("BTN post失敗");
        };
        if (cancel)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => NativeButtonControl.Click(
                fixture.Root, button, 1, 1 | 512, platform, cancellation.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => NativeButtonControl.Click(
                fixture.Root, button, 1, 1 | 512, platform, default));
        }

        Assert.Equal(new uint[] { 0x201, 0x202 }, platform.Posts.Select(post => post.Message));
        Assert.Equal("priority32", platform.Events[^1]);
        Assert.Null(fixture.CallbackFailure);
    }
}
