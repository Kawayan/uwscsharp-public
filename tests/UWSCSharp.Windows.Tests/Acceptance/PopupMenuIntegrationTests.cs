using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class PopupMenuIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(256)]
    [Trait("Category", "SandboxOnly")]
    public void ObservesNativeMenuCapacityByLabelLength(int labelLength)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        nint menu = MenuControlTests.CreatePopupMenu();
        Assert.NotEqual(0, menu);
        try
        {
            string label = new('A', labelLength);
            int appended = 0;
            while (appended < 4096 && MenuControlTests.AppendMenuW(
                menu, 0, (nuint)(appended + 1), label))
            {
                appended++;
            }

            Assert.InRange(appended, 1, 4096);
            Assert.Equal(appended, NativeMenuControl.GetMenuItemCount(menu));
            output.WriteLine($"labelLength={labelLength} appended={appended}");
        }
        finally
        {
            Assert.True(MenuControlTests.DestroyMenu(menu));
        }
    }

    [Theory]
    [InlineData(false, "0", new int[] { 0x28, 0x0d })]
    [InlineData(false, "1", new int[] { 0x28, 0x28, 0x0d })]
    [InlineData(false, "-1", new int[] { 0x1b })]
    [InlineData(true, "4", new int[] { 0x28, 0x28, 0x28, 0x27, 0x28, 0x0d })]
    [InlineData(false, "1", new int[] { 0x28, 0x28, 0x0d }, true)]
    [InlineData(false, "-1", new int[] { 0x1b }, true)]
    [InlineData(true, "4", new int[] { 0x28, 0x28, 0x28, 0x27, 0x28, 0x0d }, true)]
    public async Task ScriptReturnsTheOriginalArrayPosition(
        bool nested, string expected, int[] keys, bool worker = false)
    {
        (nint Popup, IReadOnlyList<string> Output) result = await InspectScriptAsync(nested, async popup =>
        {
            var bar = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
            Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
            int step = 0;
            foreach (int key in keys)
            {
                SendMenuKey(key);

                if (key is 0x0d or 0x1b)
                {
                    output.WriteLine($"menu-terminal step={step} key={key}");
                }
                else
                {
                    nint menu = nested && step >= 3
                        ? NativeMenuControl.GetSubMenu(bar.Menu, 2) : bar.Menu;
                    int position = nested && step >= 3 ? step - 3 : step;
                    await WaitForSelectionAsync(menu, position, step, key);
                }
                step++;
            }
        }, worker: worker);
        Assert.Equal([expected], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", false, false)]
    [InlineData("UWSCSharp.Cli", true, false)]
    [InlineData("UWSCSharp.Wpf", false, false)]
    [InlineData("UWSCSharp.Wpf", true, false)]
    [InlineData("UWSCSharp.Cli", false, true)]
    [InlineData("UWSCSharp.Cli", true, true)]
    [InlineData("UWSCSharp.Wpf", false, true)]
    [InlineData("UWSCSharp.Wpf", true, true)]
    public async Task FrontendCallReturnsPopupResultToParent(
        string frontend, bool cancelMenu, bool worker)
    {
        DirectoryInfo directory =
            Directory.CreateTempSubdirectory("uwsc-popup-call-");
        try
        {
            var encoding = new UTF8Encoding(true);
            string script = Path.Combine(directory.FullName, "main.uws");
            string source = worker ? """
                PUBLIC answer=EMPTY
                PUBLIC finished=FALSE
                THREAD Choose()
                WHILE !finished
                    SLEEP(0.01)
                WEND
                PRINT answer
                PRINT VARTYPE(answer)
                PROCEDURE Choose()
                    CALL menu.uws
                    finished=TRUE
                FEND
                """ : """
                PUBLIC answer=EMPTY
                CALL menu.uws
                PRINT answer
                PRINT VARTYPE(answer)
                """;
            await File.WriteAllTextAsync(script, source, encoding);
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "menu.uws"), """
                    DIM items[3]="A","","-","B"
                    answer=POPUPMENU(items,500,300)
                    """, encoding);

            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                nint popup = await FindPopupAsync(process.Id);
                if (!cancelMenu)
                {
                    var bar = new MenuBarInfo
                    {
                        Size = (uint)Marshal.SizeOf<MenuBarInfo>()
                    };
                    Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                    SendMenuKey(0x28, process.Id);
                    await WaitForSelectionAsync(
                        bar.Menu, 0, 0, 0x28, processId: process.Id);
                    SendMenuKey(0x28, process.Id);
                    await WaitForSelectionAsync(
                        bar.Menu, 2, 1, 0x28, processId: process.Id);
                }

                SendMenuKey(cancelMenu ? 0x1b : 0x0d, process.Id);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(process.ExitCode == 0, await stderr);
                Assert.Equal(cancelMenu ? "-1\n3" : "2\n3",
                    (await stdout).ReplaceLineEndings("\n").Trim());
                Assert.False(Native.IsWindow(popup));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(-4, "", "1")]
    [InlineData(-4, "-", "2")]
    [InlineData(5, "", "1")]
    [InlineData(5, "-", "2")]
    [Trait("Category", "SandboxOnly")]
    public async Task BoundedSafeArrayReturnsZeroBasedSelection(
        int lower, string middle, string expected)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string setup = $"""
            DIM items=SAFEARRAY({lower},{lower + 2},12)
            items[{lower}]="A"
            items[{lower + 1}]="{middle}"
            items[{lower + 2}]="B"
            """;
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, async popup =>
            {
                var bar = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
                Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                SendMenuKey(0x28);
                await WaitForSelectionAsync(bar.Menu, 0, 0, 0x28);
                SendMenuKey(0x28);
                int position = middle.Length == 0 ? 1 : 2;
                await WaitForSelectionAsync(bar.Menu, position, 1, 0x28);
                SendMenuKey(0x0d);
            }, customSetup: setup);
        Assert.Equal([expected], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Theory]
    [InlineData(0, "2")]
    [InlineData(1, "2")]
    [InlineData(3, "3")]
    [InlineData(16, "16")]
    public async Task ClickingMenuItemReturnsItsSelectionIndex(
        int depth, string expected)
    {
        string[] items = depth switch
        {
            0 => ["A", "", "-", "B"],
            1 => ["親", "{A", "B}"],
            _ => ["親", .. Enumerable.Repeat("{子", depth - 1),
                "{選択}", .. Enumerable.Repeat("戻る}", depth - 1)]
        };
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, async popup =>
            {
                var bar = new MenuBarInfo
                {
                    Size = (uint)Marshal.SizeOf<MenuBarInfo>()
                };
                Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                nint itemMenu = bar.Menu;
                nint expectedWindow = popup;
                for (int level = 0; level < depth; level++)
                {
                    await WaitToMoveToMenuItemAsync(
                        itemMenu, 0, expectedWindow);
                    itemMenu = NativeMenuControl.GetSubMenu(itemMenu, 0);
                    Assert.NotEqual(0, itemMenu);
                    expectedWindow = 0;
                }

                uint item = depth switch
                {
                    0 => 2u,
                    1 => 1u,
                    _ => 0u
                };
                await WaitToMoveToMenuItemAsync(
                    itemMenu, item, expectedWindow);
                SendLeftClick();
            }, items);
        Assert.Equal([expected], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Theory]
    [InlineData(0, 0, "1")]
    [InlineData(7, 510, "4095")]
    public async Task DisplaysTheMaximumArraySizeAcrossSubmenus(
        int parent, int leaf, string expected)
    {
        string[] items = [.. Enumerable.Range(0, 8)
            .SelectMany(group => new[] { $"グループ {group}" }
                .Concat(Enumerable.Range(0, 511).Select(index => index switch
                {
                    0 => "{最初",
                    510 => "最後}",
                    _ => $"項目 {index}"
                })))];
        Assert.Equal(4096, items.Length);

        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, async popup =>
            {
                var bar = new MenuBarInfo
                {
                    Size = (uint)Marshal.SizeOf<MenuBarInfo>()
                };
                Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                Assert.Equal(8, NativeMenuControl.GetMenuItemCount(bar.Menu));
                for (int group = 0; group < 8; group++)
                {
                    nint child = NativeMenuControl.GetSubMenu(bar.Menu, group);
                    Assert.NotEqual(0, child);
                    Assert.Equal(511, NativeMenuControl.GetMenuItemCount(child));
                }

                int parentKey = parent == 0 ? 0x28 : 0x26;
                SendMenuKey(parentKey);
                await WaitForSelectionAsync(bar.Menu, parent, 0, parentKey);
                SendMenuKey(0x27);
                nint childMenu = NativeMenuControl.GetSubMenu(bar.Menu, parent);
                await WaitForSelectionAsync(childMenu, 0, 1, 0x27, 511);
                if (leaf != 0)
                {
                    SendMenuKey(0x26);
                    await WaitForSelectionAsync(childMenu, leaf, 2, 0x26, 511);
                }

                SendMenuKey(0x0d);
            }, items);
        Assert.Equal([expected], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Fact]
    public async Task ClickingOutsidePopupReturnsCancellation()
    {
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, popup =>
            {
                using var dpi = new PhysicalDpiScope();
                Native.Point point = new NativeDisplayPlatform()
                    .CursorPosition();
                nint target = Native.WindowFromPoint(point);
                Assert.NotEqual(0, target);
                Assert.NotEqual(popup, target);
                Native.GetWindowThreadProcessId(target, out uint process);
                Assert.Equal((uint)Environment.ProcessId, process);
                SendLeftClick();
                return Task.CompletedTask;
            });
        Assert.Equal(["-1"], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Fact]
    public async Task OffScreenNegativeCoordinatesStillAllowSelection()
    {
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, popup =>
            {
                using var dpi = new PhysicalDpiScope();
                Assert.True(Native.GetWindowRect(popup, out Native.Rect bounds));
                nint monitor = Native.MonitorFromWindow(popup, 2);
                var info = new Native.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
                };
                Assert.True(Native.GetMonitorInfoW(monitor, ref info));
                Assert.InRange(bounds.Left, info.Work.Left, info.Work.Right - 1);
                Assert.InRange(bounds.Top, info.Work.Top, info.Work.Bottom - 1);
                var bar = new MenuBarInfo
                {
                    Size = (uint)Marshal.SizeOf<MenuBarInfo>()
                };
                Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                Assert.True(MoveToMenuItem(bar.Menu, 1, popup));
                SendLeftClick();
                return Task.CompletedTask;
            }, x: -10000, y: -10000);
        Assert.Equal(["1"], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Theory]
    [InlineData(160, 180)]
    [InlineData(320, 260)]
    public async Task PopupUsesRequestedPhysicalCoordinates(int x, int y)
    {
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, popup =>
            {
                using var dpi = new PhysicalDpiScope();
                Assert.True(Native.GetWindowRect(popup, out Native.Rect bounds));
                Assert.InRange(bounds.Left, x - 8, x + 8);
                Assert.InRange(bounds.Top, y - 8, y + 8);
                SendMenuKey(0x1b);
                return Task.CompletedTask;
            }, x: x, y: y);
        Assert.Equal(["-1"], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(80, null)]
    [InlineData(null, 90)]
    public async Task OmittedCoordinateUsesMousePosition(int? x, int? y)
    {
        Native.Point anchor = default;
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, popup =>
            {
                using var dpi = new PhysicalDpiScope();
                Assert.True(Native.GetWindowRect(popup, out Native.Rect bounds));
                Assert.InRange(bounds.Left, anchor.X - 8, anchor.X + 8);
                Assert.InRange(bounds.Top, anchor.Y - 8, anchor.Y + 8);
                SendMenuKey(0x1b);
                return Task.CompletedTask;
            }, x: x, y: y, prepareCursor: root =>
            {
                nint monitor = Native.MonitorFromWindow(root, 2);
                var info = new Native.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
                };
                Assert.True(Native.GetMonitorInfoW(monitor, ref info));
                anchor = new Native.Point(
                    info.Work.Left + (info.Work.Right - info.Work.Left) / 3,
                    info.Work.Top + (info.Work.Bottom - info.Work.Top) / 3);
                Assert.True(Native.SetPhysicalCursorPos(anchor.X, anchor.Y));
                Assert.True(Native.GetPhysicalCursorPos(out Native.Point actual));
                Assert.Equal(anchor, actual);
            });
        Assert.Equal(["-1"], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    private static void SendLeftClick()
    {
        foreach (uint flags in new uint[] { 2, 4 })
        {
            NativeInputSender.Send(new Native.Input
            {
                Type = 0,
                Data = new Native.InputUnion
                {
                    Mouse = new Native.MouseInput { Flags = flags }
                }
            });
        }
    }

    private static async Task WaitToMoveToMenuItemAsync(
        nint menu, uint item, nint popup)
    {
        var timer = Stopwatch.StartNew();
        while (!MoveToMenuItem(menu, item, popup))
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4),
                "メニュー項目がマウスで選べる位置に表示されません。");
            await Task.Delay(25);
        }
    }

    private static bool MoveToMenuItem(nint menu, uint item, nint popup)
    {
        using var dpi = new PhysicalDpiScope();
        if (!GetMenuItemRect(0, menu, item, out Native.Rect bounds))
        {
            return false;
        }

        var point = new Native.Point(
            (bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        nint target = Native.WindowFromPoint(point);
        var info = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
        if ((popup != 0 && target != popup)
            || !GetMenuBarInfo(target, -4, 0, ref info)
            || info.Menu != menu)
        {
            return false;
        }

        Assert.True(Native.SetCursorPos(point.X, point.Y));
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMenuItemRect(
        nint owner, nint menu, uint item, out Native.Rect bounds);

    [Fact]
    public async Task SelectsLeafAtMaximumSubmenuDepth()
    {
        string[] items = ["root", .. Enumerable.Repeat("{branch", 15),
            "{leaf}", .. Enumerable.Repeat("last}", 15)];
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, async popup =>
            {
                var bar = new MenuBarInfo
                {
                    Size = (uint)Marshal.SizeOf<MenuBarInfo>()
                };
                Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
                nint menu = bar.Menu;
                SendMenuKey(0x28);
                await WaitForSelectionAsync(menu, 0, 0, 0x28);
                for (int depth = 1; depth <= 16; depth++)
                {
                    menu = NativeMenuControl.GetSubMenu(menu, 0);
                    SendMenuKey(0x27);
                    await WaitForSelectionAsync(menu, 0, depth, 0x27);
                }

                SendMenuKey(0x0d);
            }, items);
        Assert.Equal(["16"], result.Output);
        Assert.False(Native.IsWindow(result.Popup));
    }

    internal static void SendMenuKey(int key, int? processId = null)
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process);
        Assert.Equal((uint)(processId ?? Environment.ProcessId), process);
        uint extended = key is >= 0x21 and <= 0x28 ? 1u : 0;
        foreach (uint flags in new[] { extended, extended | 2 })
        {
            NativeInputSender.Send(new Native.Input
            {
                Type = 1,
                Data = new Native.InputUnion
                {
                    Keyboard = new Native.KeyboardInput { Vk = (ushort)key, Flags = flags }
                }
            });
        }
    }

    private async Task WaitForSelectionAsync(nint menu, int position,
        int step, int key, int maximumCount = 100, int? processId = null)
    {
        Assert.NotEqual(0, menu);
        var timer = Stopwatch.StartNew();
        bool selected = false;
        uint[] states = [];
        while (timer.Elapsed < TimeSpan.FromSeconds(4))
        {
            int count = NativeMenuControl.GetMenuItemCount(menu);
            Assert.InRange(count, 1, maximumCount);
            states = new uint[count];
            for (int index = 0; index < count; index++)
            {
                var item = new NativeMenuControl.MenuItemInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMenuControl.MenuItemInfo>(),
                    Mask = 1
                };
                Assert.True(NativeMenuControl.GetMenuItemInfoW(menu,
                    (uint)index, true, ref item));
                states[index] = item.State;
            }
            if (position < count && (states[position] & 0x80) != 0)
            {
                selected = true;
                break;
            }
            await Task.Delay(25);
        }

        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process);
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point cursor));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "popup-selection",
            step,
            key,
            menu = menu.ToInt64(),
            expectedPosition = position,
            selected,
            states,
            foregroundProcess = process,
            cursor = new[] { cursor.X, cursor.Y }
        }));
        Assert.Equal((uint)(processId ?? Environment.ProcessId), process);
        Assert.True(selected, $"メニュー項目の強調状態が成立しません: step={step}, position={position}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuBarInfo
    {
        internal uint Size;
        internal Native.Rect Bounds;
        internal nint Menu;
        internal nint Submenu;
        internal uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuBarInfo(nint window, int objectId,
        int item, ref MenuBarInfo info);

    [Fact]
    public async Task InspectionFailureCancelsTheScriptAndReleasesItsMenu()
    {
        nint popup = 0;
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InspectScriptAsync(false, handle =>
            {
                popup = handle;
                throw new InvalidOperationException("inspection failure");
            }));
        Assert.Equal("inspection failure", error.Message);
        Assert.NotEqual(0, popup);
        Assert.False(Native.IsWindow(popup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingScriptClosesPendingPopupMenu(bool worker)
    {
        using var cancellation = new CancellationTokenSource();
        (nint Popup, IReadOnlyList<string> Output) result =
            await InspectScriptAsync(false, async popup =>
            {
                Assert.True(Native.IsWindowVisible(popup));
                await cancellation.CancelAsync();
            }, worker: worker, scriptStop: cancellation.Token);
        Assert.False(Native.IsWindow(result.Popup));
        Assert.Empty(result.Output);
    }

    private static async Task<(nint Popup, IReadOnlyList<string> Output)> InspectScriptAsync(
        bool nested, Func<nint, Task> inspect, string[]? customItems = null,
        bool worker = false, CancellationToken scriptStop = default,
        int? x = 80, int? y = 90, Action<nint>? prepareCursor = null,
        string? customSetup = null)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        using (var dpi = new PhysicalDpiScope())
        {
            if (prepareCursor is null)
            {
                Assert.True(Native.GetWindowRect(
                    fixture.Root, out Native.Rect bounds));
                Assert.True(Native.SetCursorPos(
                    bounds.Right - 10, bounds.Bottom - 10));
            }
            else
            {
                prepareCursor(fixture.Root);
            }
        }

        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(scriptStop);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        List<string> output = [];
        string items = nested ? "\"aaa\",\"bbb\",\"ccc\",\"{111\",\"222\",\"333}\",\"ddd\""
            : "\"A\",\"B\",\"C\"";
        int lastIndex = nested ? 6 : 2;
        if (customItems is not null)
        {
            items = string.Join(",", customItems.Select(item => $"\"{item}\""));
            lastIndex = customItems.Length - 1;
        }

        string source = customSetup ?? $$"""
            PUBLIC items[{{lastIndex}}]={{items}}
            """;
        string selection = x is null && y is null ? "POPUPMENU(items)"
            : y is null ? FormattableString.Invariant($"POPUPMENU(items,{x})")
            : FormattableString.Invariant($"POPUPMENU(items,{x},{y})");
        source += worker ? "\n" + $$"""
            PUBLIC selection=EMPTY
            PUBLIC finished=FALSE
            THREAD Choose()
            WHILE !finished
                SLEEP(0.01)
            WEND
            PRINT selection
            PROCEDURE Choose()
                selection={{selection}}
                finished=TRUE
            FEND
            """ : "\nPRINT " + selection;
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            source, output.Add, cancellation.Token);
        nint popup = 0;
        Exception? failure = null;
        try
        {
            popup = await FindPopupAsync();
            Assert.NotEqual(0, popup);
            await inspect(popup);
            if (scriptStop.IsCancellationRequested)
            {
                OperationCanceledException error =
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => run.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            try
            {
                await cancellation.CancelAsync();
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // 検査を中断した場合も、所有する実行の取消完了まで待ちます。
                }
                catch (Exception error)
                {
                    failure = failure is null ? error : new AggregateException(failure, error);
                }
            }
            finally
            {
                using var dpi = new PhysicalDpiScope();
                Native.SetCursorPos(previous.X, previous.Y);
            }
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return (popup, output);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("owner")]
    [InlineData("dispose")]
    public async Task ShutdownEndsTheNativeMenuLoopAndReleasesItsWindow(string mode)
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        object owner = new();
        Task<Value> show = service.ShowPopupAsync(owner,
            new([new("A", 0, [])], 80, 90), "popup-shutdown", 0, cancellation.Token);
        nint popup = await FindPopupAsync();
        if (mode == "dispose")
        {
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => show);
        }
        else
        {
            if (mode == "owner")
            {
                using var ownerCancellation = new CancellationTokenSource();
                ownerCancellation.Cancel();
                await service.CloseOwnerAsync(owner, ownerCancellation.Token);
                OperationCanceledException ownerError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => show.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(ownerCancellation.Token, ownerError.CancellationToken);
            }
            else
            {
                cancellation.Cancel();
                OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => show.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
        }

        Assert.False(Native.IsWindow(popup));
    }

    [Fact]
    public async Task WaitingRequestCanBeCanceledWithoutClosingTheActiveMenu()
    {
        await using var service = new NativeDialogService();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var request = new PopupMenuRequest([new("A", 0, [])], 80, 90);
        Task<Value> first = service.ShowPopupAsync(new object(), request, "first", 0, firstCancellation.Token);
        nint popup = await FindPopupAsync();
        Task<Value> second = service.ShowPopupAsync(new object(), request, "second", 0, secondCancellation.Token);
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(first.IsCompleted);
        Assert.True(Native.IsWindow(popup));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(popup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedPopupOpensAfterTheActiveMenuEnds(bool cancelActive)
    {
        await using var service = new NativeDialogService();
        using var activeCancellation = new CancellationTokenSource();
        Task<Value> active = service.ShowPopupAsync(new object(),
            new([new("先の項目", 0, [])], 80, 90),
            "active-popup", 0, activeCancellation.Token);
        nint firstWindow = await FindPopupAsync();
        Task<Value> queued = service.ShowPopupAsync(new object(),
            new([new("次の項目", 7, [])], 320, 260),
            "queued-popup", 0, default);
        Assert.False(queued.IsCompleted);
        Assert.True(Native.IsWindowVisible(firstWindow));

        if (cancelActive)
        {
            await activeCancellation.CancelAsync();
            OperationCanceledException error =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(activeCancellation.Token, error.CancellationToken);
        }
        else
        {
            SendMenuKey(0x1b);
            Assert.Equal(Value.Numeric(-1),
                await active.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        nint secondWindow = await FindPopupAsync();
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(
                secondWindow, out Native.Rect bounds));
            Assert.InRange(bounds.Left, 312, 328);
            Assert.InRange(bounds.Top, 252, 268);
        }

        var info = new MenuBarInfo
        {
            Size = (uint)Marshal.SizeOf<MenuBarInfo>()
        };
        Assert.True(GetMenuBarInfo(secondWindow, -4, 0, ref info));
        await WaitToMoveToMenuItemAsync(info.Menu, 0, secondWindow);
        SendLeftClick();
        Assert.Equal(Value.Numeric(7),
            await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(firstWindow));
        Assert.False(Native.IsWindow(secondWindow));
    }

    [Fact]
    public async Task CancelingFirstWaitingPopupAllowsNextRequest()
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        var request = new PopupMenuRequest([new("項目", 0, [])], 80, 90);
        Task<Value> active = service.ShowPopupAsync(new object(),
            request, "active", 0, default);
        nint first = await FindPopupAsync();
        Task<Value> canceled = service.ShowPopupAsync(new object(),
            request, "canceled", 0, cancellation.Token);
        Task<Value> waiting = service.ShowPopupAsync(new object(),
            request, "waiting", 0, default);

        await cancellation.CancelAsync();
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(Native.IsWindowVisible(first));
        Assert.False(waiting.IsCompleted);

        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await active.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(first));

        nint second = await FindPopupAsync();
        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(second));
    }

    [Fact]
    public async Task ClosingActiveOwnerAllowsOtherOwnersPopup()
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        object owner = new();
        var request = new PopupMenuRequest([new("A", 0, [])], 80, 90);
        Task<Value> active = service.ShowPopupAsync(owner,
            request, "closing-owner", 0, default);
        nint first = await FindPopupAsync();
        Task<Value> queued = service.ShowPopupAsync(new object(),
            request, "other-owner", 0, default);
        Assert.False(queued.IsCompleted);

        cancellation.Cancel();
        await service.CloseOwnerAsync(owner, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => active.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(Native.IsWindow(first));

        nint second = await FindPopupAsync();
        Assert.NotEqual(first, second);
        Assert.False(queued.IsCompleted);
        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingServiceReleasesActiveAndWaitingPopups(
        bool cancelWaiting)
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        var request = new PopupMenuRequest([new("A", 0, [])], 80, 90);
        Task<Value> active = service.ShowPopupAsync(new object(),
            request, "active", 0, default);
        nint popup = await FindPopupAsync();
        Task<Value> firstWaiting = service.ShowPopupAsync(new object(),
            request, "first-waiting", 0, cancellation.Token);
        Task<Value> secondWaiting = service.ShowPopupAsync(new object(),
            request, "second-waiting", 0, default);
        Assert.False(firstWaiting.IsCompleted);
        Assert.False(secondWaiting.IsCompleted);

        Task disposal = service.DisposeAsync().AsTask();
        if (cancelWaiting)
        {
            await cancellation.CancelAsync();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Task<Value> show in new[] { active, secondWaiting })
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => show.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Exception? firstFailure = await Record.ExceptionAsync(
            () => firstWaiting.WaitAsync(TimeSpan.FromSeconds(5)));
        if (cancelWaiting && firstFailure is OperationCanceledException canceled)
        {
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
        }
        else
        {
            Assert.IsType<ObjectDisposedException>(firstFailure);
        }

        Assert.False(Native.IsWindow(popup));
    }

    [Fact]
    public async Task DialogTitleDoesNotAddAnItemToTheNativePopup()
    {
        await using IWindowsAutomationSession session =
            new WindowsAutomation().CreateSession();
        session.ConfigureDialogOption(Keywords.DlgTitle,
            [Value.String("ダイアログのタイトル")], default);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        var menu = UwscArray.Create([0], [Value.String("候補")]);
        Task<Value> show = session.InvokeAsync(BuiltinFunctionNames.PopupMenu,
            [Value.FromArray(menu), Value.Numeric(80), Value.Numeric(90)],
            cancellation.Token).AsTask();
        nint popup = await FindPopupAsync();
        var bar = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
        Assert.True(GetMenuBarInfo(popup, -4, 0, ref bar));
        Assert.Equal(1, NativeMenuControl.GetMenuItemCount(bar.Menu));

        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await show.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(popup));
    }

    [Fact]
    public async Task DisposingWaitingWorkerLeavesActivePopupOpen()
    {
        await using IWindowsAutomationSession root =
            new WindowsAutomation().CreateSession();
        await using IWindowsAutomationSession worker = root.Fork();
        await using IWindowsAutomationSession nextWorker = root.Fork();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        var menu = UwscArray.Create([0], [Value.String("A")]);
        Value[] arguments =
            [Value.FromArray(menu), Value.Numeric(80), Value.Numeric(90)];
        Task<Value> active = root.InvokeAsync(BuiltinFunctionNames.PopupMenu,
            arguments, cancellation.Token).AsTask();
        nint popup = await FindPopupAsync();
        Task<Value> waiting = worker.InvokeAsync(
            BuiltinFunctionNames.PopupMenu, arguments, default).AsTask();
        Assert.False(waiting.IsCompleted);
        Value[] nextArguments =
            [Value.FromArray(menu), Value.Numeric(320), Value.Numeric(260)];
        Task<Value> next = nextWorker.InvokeAsync(BuiltinFunctionNames.PopupMenu,
            nextArguments, cancellation.Token).AsTask();

        await worker.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(active.IsCompleted);
        Assert.False(next.IsCompleted);
        Assert.True(Native.IsWindowVisible(popup));

        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await active.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(popup));
        nint nextPopup = await FindPopupAsync();
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(nextPopup, out Native.Rect bounds));
            Assert.InRange(bounds.Left, 312, 328);
            Assert.InRange(bounds.Top, 252, 268);
        }

        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(nextPopup));
    }

    [Fact]
    public async Task DisposingActiveWorkerOpensWaitingParentPopup()
    {
        await using IWindowsAutomationSession root =
            new WindowsAutomation().CreateSession();
        await using IWindowsAutomationSession worker = root.Fork();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        var menu = UwscArray.Create([0], [Value.String("A")]);
        Value[] arguments =
            [Value.FromArray(menu), Value.Numeric(80), Value.Numeric(90)];
        Task<Value> active = worker.InvokeAsync(
            BuiltinFunctionNames.PopupMenu, arguments,
            cancellation.Token).AsTask();
        nint first = await FindPopupAsync();
        Task<Value> waiting = root.InvokeAsync(
            BuiltinFunctionNames.PopupMenu, arguments,
            cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        await worker.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => active.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(first));
        Assert.False(waiting.IsCompleted);
        nint second = await FindPopupAsync();
        Assert.NotEqual(first, second);
        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(second));
    }

    [Fact]
    public async Task SharedCancellationReleasesActiveAndWaitingPopups()
    {
        await using IWindowsAutomationSession root =
            new WindowsAutomation().CreateSession();
        await using IWindowsAutomationSession firstWorker = root.Fork();
        await using IWindowsAutomationSession secondWorker = root.Fork();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        var menu = UwscArray.Create([0], [Value.String("A")]);
        Value[] arguments =
            [Value.FromArray(menu), Value.Numeric(80), Value.Numeric(90)];
        Task<Value> active = root.InvokeAsync(BuiltinFunctionNames.PopupMenu,
            arguments, cancellation.Token).AsTask();
        nint popup = await FindPopupAsync();
        Task<Value> firstWaiting = firstWorker.InvokeAsync(
            BuiltinFunctionNames.PopupMenu, arguments,
            cancellation.Token).AsTask();
        Task<Value> secondWaiting = secondWorker.InvokeAsync(
            BuiltinFunctionNames.PopupMenu, arguments,
            cancellation.Token).AsTask();
        Assert.False(firstWaiting.IsCompleted);
        Assert.False(secondWaiting.IsCompleted);
        Assert.False(cancellation.IsCancellationRequested);

        await cancellation.CancelAsync();

        foreach (Task<Value> show in new[] { active, firstWaiting, secondWaiting })
        {
            OperationCanceledException error =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => show.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.False(Native.IsWindow(popup));
    }

    [Fact]
    public async Task CancelingScriptReleasesConcurrentThreadPopups()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        var requestsReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var automation = WindowsAutomation.WithHostWindows(
            (kind, _) =>
            {
                Assert.Equal(WindowsHostWindow.Main, kind);
                if (Interlocked.Increment(ref requests) == 3)
                {
                    requestsReached.TrySetResult();
                }

                return ValueTask.FromResult((nint)0);
            });
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task run = new Interpreter(windows: automation).RunAsync("""
            THREAD Choose()
            THREAD Choose()
            THREAD Choose()
            WHILE TRUE
                SLEEP(0.01)
            WEND
            PROCEDURE Choose()
                DIM items[0]="A"
                PRINT POPUPMENU(items,80,90)
            FEND
            """, lines.Enqueue, cancellation.Token);
        try
        {
            nint popup = await FindPopupAsync();
            await requestsReached.Task.WaitAsync(cancellation.Token);
            Assert.False(run.IsCompleted);
            Assert.True(Native.IsWindowVisible(popup));

            await cancellation.CancelAsync();

            OperationCanceledException error =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(lines);
            Assert.False(Native.IsWindow(popup));
        }
        finally
        {
            if (!run.IsCompleted)
            {
                await cancellation.CancelAsync();
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task FlatMenuCapacityFailureReleasesTheNextRequest()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using var service = new NativeDialogService();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        PopupMenuItem[] items = [.. Enumerable.Range(0, 4096)
            .Select(index => new PopupMenuItem("item", index, []))];

        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(
            () => service.ShowPopupAsync(new object(),
                new(items, 80, 90), "flat-popup", 0, timeout.Token));
        Assert.StartsWith("メニュー項目を追加できませんでした（添字: ", error.Message);

        Task<Value> next = service.ShowPopupAsync(new object(),
            new([new("A", 0, [])], 80, 90), "next-popup", 0, timeout.Token);
        nint popup = await FindPopupAsync();
        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(popup));
    }

    [Fact]
    public async Task CancellationDuringPopupConstructionReleasesTheNextRequest()
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        var items = new CancelingPopupItems(cancellation);
        Task<Value> building = service.ShowPopupAsync(new object(),
            new(items, 80, 90), "building-popup", 0, cancellation.Token);
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => building.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);

        Task<Value> next = service.ShowPopupAsync(new object(),
            new([new("A", 0, [])], 80, 90), "next-popup", 0, default);
        nint popup = await FindPopupAsync();
        SendMenuKey(0x1b);
        Assert.Equal(Value.Numeric(-1),
            await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Native.IsWindow(popup));
    }

    private sealed class CancelingPopupItems(CancellationTokenSource source)
        : IReadOnlyList<PopupMenuItem>
    {
        public int Count => 3;

        public PopupMenuItem this[int index]
        {
            get
            {
                Assert.InRange(index, 0, 1);
                if (index == 1)
                {
                    source.Cancel();
                }

                return new("A", index, []);
            }
        }

        public IEnumerator<PopupMenuItem> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static async Task<nint> FindPopupAsync(int? processId = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            nint found = 0;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint process);
                var name = new StringBuilder(256);
                Native.GetClassNameW(window, name, name.Capacity);
                if (process == (processId ?? Environment.ProcessId)
                    && name.ToString() == "#32768"
                    && Native.IsWindowVisible(window))
                {
                    found = window;
                    return false;
                }

                return true;
            }, 0);
            if (found != 0)
            {
                await Task.Delay(100);
                return found;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("所有プロセスのメニューが表示されませんでした。");
    }
}
