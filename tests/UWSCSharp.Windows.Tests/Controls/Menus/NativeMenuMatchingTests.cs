using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeMenuMatchingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [Trait("Category", "SandboxOnly")]
    public async Task RecordedMenuCommandReachesNativeWindow(
        bool replaceItem, bool releaseOnOtherItem = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var received = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        string title = "記録メニュー-" + Guid.NewGuid().ToString("N");
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                owner.MenuCommand = command =>
                {
                    Interlocked.Increment(ref notifications);
                    received.TrySetResult(command);
                };
            }, title: title);
        nint menu = 0;
        string source = "";
        try
        {
            fixture.Invoke(() =>
            {
                menu = MenuControlTests.CreateMenu();
                Assert.NotEqual(0, menu);
                Assert.True(MenuControlTests.AppendMenuW(menu, 0, 701, "実行"));
                Assert.True(MenuControlTests.AppendMenuW(menu, 0, 702, "別の操作"));
                Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
                Assert.True(GetMenuItemRect(fixture.Root, menu, 0, out Native.Rect bounds));
                int x = (bounds.Left + bounds.Right) / 2;
                int y = (bounds.Top + bounds.Bottom) / 2;
                var background = new RecordingBuffer([], 0)
                {
                    RecordButtonClicks = true,
                    BackgroundPlayback = true
                };
                background.Mouse(0, 1, x, y, 100, false, buttonTarget: fixture.Root);
                background.Mouse(0, 2, x, y, 200, false, buttonTarget: fixture.Root);
                background.Complete();
                Assert.All(background.Events, input => Assert.Null(input.MenuClick));
                Assert.Empty(BasicRecorder.Serialize(background.Events,
                    backgroundPlayback: true));

                var buffer = new RecordingBuffer([], 0)
                {
                    RecordButtonClicks = true
                };
                buffer.Mouse(0, 1, x, y, 100, false, buttonTarget: fixture.Root);
                if (replaceItem)
                {
                    Assert.True(ModifyMenuW(menu, 0, 0x400, 703, "実行"));
                    Assert.True(GetMenuItemRect(fixture.Root, menu, 0,
                        out Native.Rect updated));
                    Assert.Equal(0, NativeMenuControl.MenuItemFromPoint(
                        fixture.Root, menu, new Native.Point(x, y)));
                    Assert.InRange(x, updated.Left, updated.Right - 1);
                    Assert.InRange(y, updated.Top, updated.Bottom - 1);
                }

                if (releaseOnOtherItem)
                {
                    Assert.True(GetMenuItemRect(fixture.Root, menu, 1,
                        out Native.Rect other));
                    x = (other.Left + other.Right) / 2;
                    y = (other.Top + other.Bottom) / 2;
                    Assert.Equal(1, NativeMenuControl.MenuItemFromPoint(
                        fixture.Root, menu, new Native.Point(x, y)));
                }

                buffer.Mouse(0, 2, x, y, 200, false, buttonTarget: fixture.Root);
                buffer.Complete();
                if (replaceItem || releaseOnOtherItem)
                {
                    Assert.Collection(buffer.Events,
                        down => Assert.Equal(1, down.State),
                        up => Assert.Equal(2, up.State));
                    Assert.DoesNotContain("CLKITEM",
                        BasicRecorder.Serialize(buffer.Events), StringComparison.Ordinal);
                    return;
                }

                Assert.NotNull(Assert.Single(buffer.Events).MenuClick);
                source = "PRINT " + BasicRecorder.Serialize(buffer.Events);
                // 記録した項目を2番目へ移し、元の位置には別の項目を置きます。
                Assert.True(ModifyMenuW(menu, 0, 0x400, 702, "別の操作"));
                Assert.True(ModifyMenuW(menu, 1, 0x400, 701, "実行"));
                Assert.Equal(702u, NativeMenuControl.GetMenuItemID(menu, 0));
                Assert.Equal(701u, NativeMenuControl.GetMenuItemID(menu, 1));
            });

            if (replaceItem || releaseOnOtherItem)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation())
                .RunAsync(source, output.Add, deadline.Token);
            Assert.Equal(["1"], output);
            Assert.Equal(701, await received.Task.WaitAsync(deadline.Token));

            fixture.Invoke(() =>
            {
                Assert.True(RemoveMenu(menu, 1, 0x400));
                Assert.Equal(1, NativeMenuControl.GetMenuItemCount(menu));
                Assert.Equal(702u, NativeMenuControl.GetMenuItemID(menu, 0));
            });
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation())
                .RunAsync(source, output.Add, deadline.Token);
            Assert.Equal(["0"], output);
            fixture.Invoke(() => Assert.Equal(1, Volatile.Read(ref notifications)));
        }
        finally
        {
            if (menu != 0)
            {
                fixture.Invoke(() =>
                {
                    MenuControlTests.SetMenu(fixture.Root, 0);
                    MenuControlTests.DestroyMenu(menu);
                });
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMenuItemRect(
        nint window, nint menu, uint item, out Native.Rect bounds);

    [System.Runtime.InteropServices.DllImport("user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ModifyMenuW(
        nint menu, uint position, uint flags, nuint command, string label);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RemoveMenu(nint menu, uint position, uint flags);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    [InlineData(false, true)]
    public async Task RecordedSubmenuPathDispatchesToOwnedWindow(
        bool disabled, bool duplicate = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var received = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                owner.MenuCommand = command => received.TrySetResult(command);
            }, title: "サブメニュー再生-" + Guid.NewGuid().ToString("N"));
        nint menu = 0;
        try
        {
            RecordedMenuClick? recorded = null;
            fixture.Invoke(() =>
            {
                menu = MenuControlTests.CreateMenu();
                Assert.NotEqual(0, menu);
                nint submenu = MenuControlTests.CreatePopupMenu();
                Assert.NotEqual(0, submenu);
                if (!MenuControlTests.AppendMenuW(menu, 0x10,
                    (nuint)submenu, "操作"))
                {
                    MenuControlTests.DestroyMenu(submenu);
                    Assert.Fail("サブメニューを追加できませんでした。");
                }

                Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
                Assert.True(MenuControlTests.AppendMenuW(submenu, 0, 701, "実行"));
                Assert.True(GetMenuItemRect(fixture.Root, menu, 0,
                    out Native.Rect heading));
                int x = (heading.Left + heading.Right) / 2;
                int y = (heading.Top + heading.Bottom) / 2;
                var buffer = new RecordingBuffer([], 0)
                {
                    RecordButtonClicks = true
                };
                buffer.Mouse(0, 1, x, y, 100, false, buttonTarget: fixture.Root);
                buffer.Mouse(0, 2, x, y, 200, false, buttonTarget: fixture.Root);
                buffer.Complete();
                Assert.Collection(buffer.Events,
                    down => Assert.Equal(1, down.State),
                    up => Assert.Equal(2, up.State));
                Assert.DoesNotContain("CLKITEM",
                    BasicRecorder.Serialize(buffer.Events), StringComparison.Ordinal);

                recorded = RecordedMenuClick.Capture(fixture.Root, 701);
                uint flags = disabled ? 0x403u : 0x400u;
                Assert.True(ModifyMenuW(submenu, 0, flags, 703, "実行"));
                if (duplicate)
                {
                    Assert.True(MenuControlTests.AppendMenuW(
                        submenu, 0, 701, "実行"));
                    Assert.Null(RecordedMenuClick.Capture(fixture.Root, 701));
                }
            });
            Assert.NotNull(recorded);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation())
                .RunAsync("PRINT " + recorded.Command(), output.Add, deadline.Token);
            if (disabled)
            {
                Assert.Equal(["0"], output);
                fixture.Invoke(() => Assert.False(received.Task.IsCompleted));
            }
            else
            {
                Assert.Equal(["1"], output);
                Assert.Equal(703, await received.Task.WaitAsync(deadline.Token));
            }
        }
        finally
        {
            if (menu != 0)
            {
                fixture.Invoke(() =>
                {
                    MenuControlTests.SetMenu(fixture.Root, 0);
                    MenuControlTests.DestroyMenu(menu);
                });
            }
        }
    }

    [Fact]
    public async Task RecordingFindsUniqueMenuPathWithoutInitializingSubmenus()
    {
        string title = "メニュー記録-" + Guid.NewGuid().ToString("N");
        using var fixture = new ButtonControlTests.Fixture(title);
        nint menu = MenuControlTests.CreateMenu();
        nint popup = MenuControlTests.CreatePopupMenu();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, 0x10,
                (nuint)popup, "操作"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            Assert.True(MenuControlTests.AppendMenuW(popup, 0, 701, "実行"));
            Assert.True(MenuControlTests.AppendMenuW(popup, 3, 702, "無効"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 0, 704, "操作\\実行"));
            int initializations = 0;
            fixture.MenuInit = (_, _, _) => initializations++;

            Assert.Equal("操作\\実行",
                NativeMenuControl.FindRecordingItem(fixture.Root, 701)?.Path);
            var recorded = RecordedMenuClick.Capture(fixture.Root, 701);
            Assert.NotNull(recorded);
            Assert.Null(NativeMenuControl.FindRecordingItem(fixture.Root, 702));
            Assert.Null(RecordedMenuClick.Capture(fixture.Root, 704));
            Assert.Equal(0, initializations);

            var platform = new NativeMenuOperationTests.Platform();
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException(),
                listSelectionPlatform: platform))
                .RunAsync("PRINT " + recorded.Command(), output.Add);
            Assert.Equal(["1"], output);
            Assert.Equal((nint)701, Assert.Single(platform.Posts).Parameter);

            Assert.True(MenuControlTests.AppendMenuW(popup, 0, 703, "実行"));
            Assert.Null(RecordedMenuClick.Capture(fixture.Root, 701));
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 702)]
    [InlineData(0, 702)]
    [InlineData(-1, 0)]
    [InlineData(-2, 702)]
    public async Task DisabledNameCountsBeforeStateCheck(int number, int command)
    {
        await Verify("disabled", "target", number, command);
    }

    [Theory]
    [InlineData("target", 1, 701)]
    [InlineData("target", 2, 702)]
    [InlineData("target", 3, 0)]
    [InlineData("first\\target", 2, 702)]
    [InlineData("second\\target", 1, 703)]
    public async Task CountsRestartInEachRecursiveMenu(string name, int number, int command)
    {
        await Verify("recursive", name, number, command);
    }

    [Theory]
    [InlineData("target", 1, 701)]
    [InlineData("first\\target", 1, 701)]
    public async Task DisabledHeadingDoesNotFilterItsChildren(string name, int number, int command)
    {
        await Verify("disabled-heading", name, number, command);
    }

    [Fact]
    public async Task MatchingLeafStopsIntermediatePathLookup()
    {
        await Verify("shadow-heading", "first\\target", 1, 0);
    }

    [Theory]
    [InlineData("&Run...", "run", false, 701)]
    [InlineData("ＡＢＣ", "ABC", false, 701)]
    [InlineData("prefix target suffix", "target", true, 701)]
    [InlineData("ＡＢＣsuffix", "ABC", true, 701)]
    [InlineData("ä", "Ä", false, 0)]
    [InlineData("target", "\\target", false, 0)]
    public async Task MatchesAsciiHalfWidthShortAndLiteralSingleName(
        string text, string name, bool partial, int command)
    {
        await Verify("name", name, 1, command, text, partial);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(256, false)]
    [InlineData(300, true)]
    [InlineData(300, false)]
    public async Task ClickUsesFixedNamePrefix(int length, bool prefix)
    {
        string text = new('長', length);
        await Verify("name", prefix ? text[..255] : text,
            1, prefix ? 701 : 0, text);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("cancel-init")]
    [InlineData("cancel-pump")]
    [InlineData("throw-pump")]
    [InlineData("disable-init")]
    public async Task SubmenuIsInitializedAfterMatchBeforeStateWithLocalCatch(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        nint popup = MenuControlTests.CreatePopupMenu();
        using var cancel = new CancellationTokenSource();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(popup, 0, 700, "other"));
            Assert.True(MenuControlTests.AppendMenuW(popup, 0, 701, "target"));
            Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)popup, "parent"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new NativeMenuOperationTests.Platform();
            List<string> events = platform.Events;
            fixture.MenuInit = (message, target, index) =>
            {
                events.Add("init" + message);
                if (message == 0x117)
                {
                    Assert.Equal(popup, target);
                    Assert.Equal(1, index);
                    Assert.Equal("activate", events[^2]);
                    if (outcome == "cancel-init")
                    {
                        cancel.Cancel();
                    }
                    else if (outcome == "disable-init")
                    {
                        Assert.NotEqual(uint.MaxValue, EnableMenuItem(popup, 1, 0x403));
                    }
                    else if (outcome is "cancel-pump" or "throw-pump")
                    {
                        platform.Pumping = () =>
                        {
                            platform.Pumping = null;
                            if (outcome == "cancel-pump")
                            {
                                cancel.Cancel();
                            }
                            else
                            {
                                throw new InvalidOperationException("popup待機失敗");
                            }
                        };
                    }
                }
            };
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Value[] args = [id, Value.String("target"), Value.Numeric(4)];
            if (outcome.StartsWith("cancel", StringComparison.Ordinal))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await session.InvokeAsync("CLKITEM", args, cancel.Token));
            }
            else
            {
                Assert.Equal(Value.Numeric(outcome == "disable-init" ? 0 : 1),
                    await session.InvokeAsync("CLKITEM", args, default));
            }

            Assert.Equal(outcome is "normal" or "throw-pump" ? 1 : 0, platform.Posts.Count);
            if (outcome == "normal")
            {
                Assert.Equal(14, events.Count(item => item == "pump"));
            }

            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint EnableMenuItem(nint menu, uint item, uint flags);

    [Fact]
    public async Task IntermediatePathUsesFirstHeadingRegardlessOfOccurrence()
    {
        await Verify("duplicate-heading", "first\\target", 2, 0);
    }

    [Fact]
    public async Task FinalPathMayMatchPopupHeadingAndPostUnsignedId()
    {
        await Verify("popup-final", "first\\target", 1, uint.MaxValue);
    }

    [Fact]
    public async Task MinimumIntegerFailsBeforeMenuInitialization()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        int initializations = 0;
        fixture.MenuInit = (_, _, _) => initializations++;
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(),
            listSelectionPlatform: new NativeMenuOperationTests.Platform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("target"),
                Value.Numeric(4), Value.Numeric(1), Value.Numeric(int.MinValue)], default));
        Assert.Equal(0, initializations);
        Assert.Null(fixture.CallbackFailure);
    }

    private static async Task Verify(string layout, string name, int number, long command,
        string? text = null, bool partial = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        try
        {
            if (layout == "popup-final")
            {
                nint first = MenuControlTests.CreatePopupMenu();
                nint nested = MenuControlTests.CreatePopupMenu();
                Assert.True(MenuControlTests.AppendMenuW(nested, 0, 701, "other"));
                Assert.True(MenuControlTests.AppendMenuW(first, 0x10, (nuint)nested, "target"));
                Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)first, "first"));
            }
            else if (layout == "name")
            {
                Assert.True(MenuControlTests.AppendMenuW(menu, 0, 701, text));
            }
            else if (layout == "disabled")
            {
                Assert.True(MenuControlTests.AppendMenuW(menu, 3, 701, "target"));
                Assert.True(MenuControlTests.AppendMenuW(menu, 0, 702, "target"));
            }
            else
            {
                nint first = MenuControlTests.CreatePopupMenu();
                nint second = MenuControlTests.CreatePopupMenu();
                Assert.True(MenuControlTests.AppendMenuW(first, 0, 701, "target"));
                if (layout != "duplicate-heading")
                {
                    Assert.True(MenuControlTests.AppendMenuW(first, 0, 702, "target"));
                }
                Assert.True(MenuControlTests.AppendMenuW(second, 0, 703, "target"));
                Assert.True(MenuControlTests.AppendMenuW(second, 0, 704, "target"));
                if (layout == "shadow-heading")
                {
                    Assert.True(MenuControlTests.AppendMenuW(menu, 0, 700, "first"));
                }

                Assert.True(MenuControlTests.AppendMenuW(menu,
                    layout == "disabled-heading" ? 0x13u : 0x10u, (nuint)first, "first"));
                Assert.True(MenuControlTests.AppendMenuW(menu, 0x10, (nuint)second,
                    layout == "duplicate-heading" ? "first" : "second"));
            }

            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            var platform = new NativeMenuOperationTests.Platform();
            List<string> output = [];
            string source = $"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "{name}", {4 | 512 | 536870912 | (partial ? 256 : 0)}, TRUE, {number})
                """;
            if (text?.Length > 255)
            {
                source += "\nFOR entry IN GETITEM(id, ITM_MENU)\nPRINT entry\nNEXT";
            }

            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform))
                .RunAsync(source, output.Add);
            string result = command == 0 ? "0" : "1";
            Assert.Equal(text?.Length > 255 ? new[] { result, text! } : [result], output);
            Assert.Equal(command == 0 ? [] : new[] { unchecked((nint)command) },
                platform.Posts.Select(post => post.Parameter));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }
}
