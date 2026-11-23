using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ButtonControlTests
{
    [Theory]
    [InlineData("保存(&S):... ", "保存")]
    [InlineData("保存(_S)", "保存")]
    [InlineData("保存(S)", "保存")]
    [InlineData("保存(&S)\tCtrl+S", "保存")]
    [InlineData("  &Save...  ", "Save")]
    [InlineData("Save && Close", "Save  Close")]
    [InlineData("A:", "A:")]
    [InlineData("&A", "&A")]
    [InlineData("...", "")]
    [InlineData("保存(SS)", "保存(SS)")]
    [InlineData("\u0001保存\u0002", "保存")]
    [InlineData("保存\u00a0", "保存\u00a0")]
    public void NormalizesAdoptedNames(string text, string expected)
    {
        Assert.Equal(expected, ButtonName.Normalize(text));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public async Task ManualButtonsNotifyParentWithoutForcingState(
        int style, bool handlesNotification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        nint button = fixture.Child("BUTTON", "保存(&S):... ", style, 10);
        List<(nint Sender, int Id, int Code, int Before)> notifications = [];
        fixture.Clicked = (sender, command) =>
        {
            int before = (int)Message(sender, 0xf0);
            notifications.Add((sender, command & 0xffff, command >> 16, before));
            if (handlesNotification)
            {
                int after = style == 5 ? (before + 1) % 3 : 1 - before;
                Message(sender, 0xf1, after);
            }
        };
        await using IWindowsAutomationSession session = fixture.Session(new OwnedButtonMessagePlatform());
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Value result = await session.InvokeAsync("CLKITEM",
            [id, Value.String("保存")], default);
        Assert.Equal(Value.Numeric(1), result);
        Assert.Equal((button, 42, 0, 0), Assert.Single(notifications));
        Assert.Equal(handlesNotification ? 1 : 0, (int)Message(button, 0xf0));
        Assert.Equal(Value.Numeric(handlesNotification ? 1 : 0),
            await session.InvokeAsync("CHKBTN", [id, Value.String("保存")], default));
        if (handlesNotification)
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("保存")], default));
            Assert.Single(notifications);
            Value off = await session.InvokeAsync("CLKITEM",
                [id, Value.String("保存"), Value.Numeric(1), Value.Numeric(0)],
                default);
            Assert.Equal(Value.Numeric(1), off);
            Assert.Equal(2, notifications.Count);
        }

        using var cancellation = new CancellationTokenSource();
        fixture.Clicked = (_, _) => cancellation.Cancel();
        Message(button, 0xf1, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM",
                [id, Value.String("保存")], cancellation.Token));
        Assert.Equal(0, (int)Message(button, 0xf0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task GeometryTiesReverseEnumerationForTextAndButtons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        nint first = fixture.Child("BUTTON", "同名", 3, 10);
        nint second = fixture.Child("BUTTON", "同名", 3, 10);
        fixture.Child("EDIT", "first", 0, 60);
        fixture.Child("EDIT", "second", 0, 60);
        List<nint> buttons = [];
        List<nint> edits = [];
        Native.EnumChildWindows(fixture.Root, (child, _) =>
        {
            if (child == first || child == second)
            {
                buttons.Add(child);
            }
            else
            {
                edits.Add(child);
            }

            return true;
        }, 0);
        Assert.Equal(2, buttons.Count);
        Assert.Equal(2, edits.Count);
        await using IWindowsAutomationSession session = fixture.Session(new OwnedButtonMessagePlatform());
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("同名")], default));
        Assert.Equal(1u, Message(buttons[1], 0xf0));
        Assert.Equal(0u, Message(buttons[0], 0xf0));
        var buffer = new System.Text.StringBuilder(100);
        Native.ReadText(edits[1], 0xd, 100, buffer, 2, 1000, out _);
        Assert.Equal(Value.String(buffer.ToString()), await session.InvokeAsync(
            "GETSTR", [id, Value.Numeric(1)], default));
    }

    [Fact]
    public async Task TextOrderUsesNumericGeometryKeyRatherThanTupleOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        nint wide = fixture.Child("EDIT", "upper", 0, 10);
        fixture.Child("EDIT", "lower", 0, 11);
        Assert.True(Native.SetWindowPos(wide, 0, 20010, 10, 150, 25, 0x14));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String("lower"), await session.InvokeAsync(
            "GETSTR", [id, Value.Numeric(1)], default));
    }

    private static nuint Message(nint target, uint message, int value = 0)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, message,
            value, 0, 2, 1000, out nuint result));
        return result;
    }

    [Fact]
    public async Task StaticZeroReadsWindowCaptionAndPositiveNumberReadsChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string caption = " ウィンドウ " + new string('長', 4097) + " ";
        string expectedCaption = caption[..300];
        using var fixture = new Fixture(caption);
        fixture.Child("STATIC", "child caption", 0, 10);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(expectedCaption), await session.InvokeAsync("GETSTR",
            [id, Value.Numeric(0), Value.Numeric(1)], default));
        Assert.Equal(Value.String("child caption"), await session.InvokeAsync("GETSTR",
            [id, Value.Numeric(1), Value.Numeric(1)], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETSTR", [id, Value.Numeric(0), Value.Numeric(1)], cancellation.Token));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT GETSTR(id, , STR_STATIC)
                PRINT GETSTR(id, 1, STR_STATIC)
                """, output.Add);
        Assert.Equal([expectedCaption, "child caption"], output);
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR",
            [id, Value.Numeric(0), Value.Numeric(1)], default)).Kind);
    }

    [Fact]
    public async Task EmptyWindowCaptionIsAnEmptyString()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture("");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
            [id, Value.Numeric(0), Value.Numeric(1)], default));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR",
            [Value.Numeric(-1), Value.Numeric(0), Value.Numeric(1)], default)).Kind);
    }

    // このfixtureが生成した親だけをsubclass化し、必ず復元します。
    internal sealed class Fixture : IDisposable
    {
        private readonly List<(nint Window, nint Previous, WindowProcedure Procedure)> inputObservers = [];
        private readonly WindowProcedure procedure;
        private readonly nint previous;
        private readonly List<nint> checkTrees = [];
        private readonly HashSet<nint> stateImages = [];
        private readonly bool physicalCoordinates;

        internal nint Root
        {
            get;
        }

        internal Action<uint, nint, nint>? InputMessage
        {
            get; set;
        }

        internal Action<nint, int>? Clicked
        {
            get; set;
        }

        internal Exception? CallbackFailure
        {
            get;
            private set;
        }

        internal Action<nint, int>? Command
        {
            get; set;
        }

        internal Action<int, nint>? MenuPositionCommand
        {
            get; set;
        }

        internal Action<int>? MenuCommand
        {
            get; set;
        }

        internal Action<uint, nint, nint>? MenuInit
        {
            get; set;
        }

        internal Func<nint, nint, nint>? Notify
        {
            get; set;
        }

        internal Func<nint, nint>? AccessibleObject
        {
            get; set;
        }

        internal Action<uint, nint, nint>? Scroll
        {
            get; set;
        }

        internal Fixture(string? title = null, uint style = 0, string rootClass = "STATIC",
            bool avoidPointer = false, bool physicalCoordinates = false)
        {
            this.physicalCoordinates = physicalCoordinates;
            using PhysicalDpiScope? creationDpi = physicalCoordinates ? new PhysicalDpiScope() : null;
            Root = CreateWindowExW(0, rootClass, title ?? "M3 notification fixture",
                style, 10, 10, 300, 200, 0, 0, 0, 0);
            Assert.NotEqual(0, Root);
            procedure = Dispatch;
            previous = SetProcedure(Root,
                Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(0, previous);
            if (avoidPointer)
            {
                // カーソルを動かさず、専用画面を反対側へ配置します。
                using var dpi = new PhysicalDpiScope();
                Assert.True(Native.GetCursorPos(out Native.Point cursor));
                Assert.True(Native.GetWindowRect(Root, out Native.Rect bounds));
                int left = Native.GetSystemMetrics(76);
                int width = Native.GetSystemMetrics(78);
                int windowWidth = bounds.Right - bounds.Left;
                int x = cursor.X >= left + windowWidth + 20
                    ? left : left + width - windowWidth;
                Assert.True(Native.SetWindowPos(Root, 0, x, bounds.Top, 0, 0,
                    Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate));
                Assert.True(Native.GetWindowRect(Root, out bounds));
                Assert.False(cursor.X >= bounds.Left && cursor.X < bounds.Right);
            }
            ShowWindow(Root, 4);
        }

        internal nint Child(string className, string text, int style, int top,
            int id = 42, int height = 25, int width = 150)
        {
            using PhysicalDpiScope? creationDpi = physicalCoordinates ? new PhysicalDpiScope() : null;
            bool checkTree = className.Equals("SysTreeView32",
                StringComparison.OrdinalIgnoreCase) && (style & 0x100) != 0;
            nint child = CreateWindowExW(0, className, text,
                0x50000000u | (uint)(checkTree ? style & ~0x100 : style), 10, top, width, height,
                Root, id, 0, 0);
            Assert.NotEqual(0, child);
            if (checkTree)
            {
                SetWindowLongW(child, -16, (nint)(0x50000000u | (uint)style));
                checkTrees.Add(child);
                CaptureStateImage(child);
            }

            return child;
        }

        internal void ObserveInput(nint window, Action<uint, nint, nint> receive)
        {
            nint original = 0;
            WindowProcedure observer = (target, message, wParam, lParam) =>
            {
                try
                {
                    if (message is >= 0x100 and <= 0x109 or >= 0x200 and <= 0x20e or 0x240)
                    {
                        receive(message, wParam, lParam);
                    }
                }
                catch (Exception exception)
                {
                    CallbackFailure = exception;
                }
                return CallWindowProcW(original, target, message, wParam, lParam);
            };
            original = SetProcedure(window, Marshal.GetFunctionPointerForDelegate(observer));
            Assert.NotEqual(0, original);
            inputObservers.Add((window, original, observer));
        }

        private void CaptureStateImage(nint tree)
        {
            if (Native.IsWindow(tree))
            {
                nint image = Native.SendMessageW(tree, 0x1108, 2, 0);
                if (image != 0)
                {
                    stateImages.Add(image);
                }
            }
        }

        internal IWindowsAutomationSession Session(IListSelectionPlatform? platform = null) =>
            new WindowsAutomation(_ => throw new InvalidOperationException(
                "物理入力は禁止です。"),
                listSelectionPlatform: platform ?? new OwnedListSelectionPlatform()).CreateSession();

        private nint Dispatch(nint window, uint message, nint wParam, nint lParam)
        {
            if (message is >= 0x100 and <= 0x109 or >= 0x200 and <= 0x20e or 0x240)
            {
                try
                {
                    InputMessage?.Invoke(message, wParam, lParam);
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブ境界へ漏らしません。
                    CallbackFailure = exception;
                }
            }

            if (message == 0x3d && unchecked((int)lParam) == -4
                && AccessibleObject is not null)
            {
                try
                {
                    return AccessibleObject(wParam);
                }
                catch (Exception exception)
                {
                    // provider生成の失敗をcallback境界へ漏らしません。
                    CallbackFailure = exception;
                    return 0;
                }
            }

            if (message == 0x4e && lParam != 0 && Notify is not null)
            {
                try
                {
                    return Notify(wParam, lParam);
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブ境界へ漏らしません。
                    CallbackFailure = exception;
                }
            }

            if (message == 0x111 && lParam != 0)
            {
                try
                {
                    Command?.Invoke(lParam, (int)wParam);
                    if (((long)wParam >> 16) == 0)
                    {
                        Clicked?.Invoke(lParam, (int)wParam);
                    }
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブcallback境界へ漏らしません。
                    CallbackFailure = exception;
                }
            }

            if ((message == 0x111 && lParam == 0) || message is 0x116 or 0x117 or 0x126)
            {
                try
                {
                    if (message == 0x111)
                    {
                        MenuCommand?.Invoke((int)wParam);
                    }
                    else if (message == 0x126)
                    {
                        MenuPositionCommand?.Invoke((int)wParam, lParam);
                    }
                    else
                    {
                        MenuInit?.Invoke(message, wParam, lParam);
                    }
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブcallback境界へ漏らしません。
                    CallbackFailure = exception;
                }
            }

            if (message is 0x114 or 0x115)
            {
                try
                {
                    Scroll?.Invoke(message, wParam, lParam);
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブcallback境界へ漏らしません。
                    CallbackFailure = exception;
                }
            }

            return CallWindowProcW(previous, window, message, wParam, lParam);
        }

        public void Dispose()
        {
            foreach ((nint window, nint original, WindowProcedure observer) in inputObservers)
            {
                if (Native.IsWindow(window))
                {
                    SetProcedure(window, original);
                }
                GC.KeepAlive(observer);
            }
            inputObservers.Clear();
            foreach (nint tree in checkTrees)
            {
                CaptureStateImage(tree);
            }

            if (Native.IsWindow(Root))
            {
                SetProcedure(Root, previous);
                DestroyWindow(Root);
            }

            // TREEの自動生成state image listはcontrol破棄後に所有者が解放します。
            foreach (nint image in stateImages)
            {
                Assert.True(ImageList_Destroy(image));
            }

            stateImages.Clear();

            GC.KeepAlive(procedure);
        }
    }

    private static nint SetProcedure(nint window, nint procedure) =>
        IntPtr.Size == 8 ? SetWindowLongPtrW(window, -4, procedure)
            : SetWindowLongW(window, -4, procedure);

    private delegate nint WindowProcedure(nint window, uint message,
        nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extendedStyle,
        string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window,
        uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("comctl32.dll")]
    private static extern bool ImageList_Destroy(nint image);
}
