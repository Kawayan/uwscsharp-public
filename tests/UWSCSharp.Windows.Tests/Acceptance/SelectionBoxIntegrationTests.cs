using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class SelectionBoxIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("control-click", "0\t2", "258")]
    [InlineData("control-space", "2", "3")]
    [Trait("Category", "SandboxOnly")]
    public async Task ScriptReceivesSelectionOfSeparatedListItems(
        string action, string expectedValue, string expectedType)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        string title = "uwsc-selection-separated-" + Guid.NewGuid();
        List<string> lines = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $$"""
            OPTION DLGTITLE="{{title}}"
            DIM selection=SLCTBOX(144,0,"probe","A","B","C")
            PRINT selection
            PRINT VARTYPE(selection)
            """, lines.Add, cancellation.Token);
        try
        {
            nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
            nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
            Assert.NotEqual(0, list);
            await UiInputDriver.ActivateAsync(window);
            await SelectionListTestInput.SelectAsync(window, list, action, output);
            Assert.Equal(action == "control-click" ? 1 : 0,
                Native.SendMessageW(list, 0x187, 0, 0));
            Assert.Equal(0, Native.SendMessageW(list, 0x187, 1, 0));
            Click(window, 4097);
            await run.WaitAsync(cancellation.Token);
            Assert.Equal([expectedValue, expectedType], lines);
            Assert.False(Native.IsWindow(window));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedSelectionCanBeClosedWithoutAnInputDialogCast(bool dispose)
    {
        await using var service = new NativeDialogService();
        object owner = new();
        string title = "uwsc-selection-owner-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(owner,
            new(1, 0, "probe", ["A"], null, null, 0), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        if (dispose)
        {
            await service.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => show);
        }
        else
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await service.CloseOwnerAsync(owner, cancellation.Token);
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => show);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.False(Native.IsWindow(window));
    }

    [Theory]
    [InlineData(1, "2", ValueKind.Number)]
    [InlineData(2, "3", ValueKind.Number)]
    [InlineData(4, "2", ValueKind.Number)]
    [InlineData(8, "2", ValueKind.Number)]
    [InlineData(16, "3", ValueKind.Number)]
    [InlineData(65, "B", ValueKind.String)]
    [InlineData(66, "A\tB", ValueKind.String)]
    [InlineData(68, "B", ValueKind.String)]
    [InlineData(72, "B", ValueKind.String)]
    [InlineData(80, "A\tB", ValueKind.String)]
    [InlineData(129, "1", ValueKind.Number)]
    [InlineData(130, "0\t1", ValueKind.String)]
    [InlineData(132, "1", ValueKind.Number)]
    [InlineData(136, "1", ValueKind.Number)]
    [InlineData(144, "0\t1", ValueKind.String)]
    public async Task EveryKindReturnsItsDocumentedSelection(int flags, string expected, ValueKind kind)
    {
        await using var service = new NativeDialogService();
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0),
            Value.String("probe"), Value.String("A"), Value.String("B")]);
        string title = "uwsc-selection-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(), request, new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        SelectSecondItem(window, request.Kind, selectBoth: true);

        Value result = await show.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(kind, result.Kind);
        Assert.Equal(expected, result.Display());
    }

    private static void SelectSecondItem(
        nint window, int kind, bool selectBoth)
    {
        if (kind is 1 or 2 or 4)
        {
            if (kind == 2 && selectBoth)
            {
                Click(window, 0x5000);
            }

            Click(window, 0x5001);
        }
        else
        {
            nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
            if (kind == 8)
            {
                Native.SendMessageTimeoutW(list, 0x14e, 1, 0, 2, 1000, out _);
            }
            else
            {
                if (selectBoth)
                {
                    Native.SendMessageTimeoutW(
                        list, 0x185, 1, 0, 2, 1000, out _);
                }

                Native.SendMessageTimeoutW(list, 0x185, 1, 1, 2, 1000, out _);
            }
        }

        if (kind != 1)
        {
            Click(window, 4097);
        }
    }

    [Theory]
    [InlineData(2, "0")]
    [InlineData(4, "1")]
    [InlineData(8, "1")]
    [InlineData(16, "0")]
    public async Task InitialSelectionMatchesTheObservedKind(int flags, string expected)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-initial-" + Guid.NewGuid();
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0),
            Value.String("probe"), Value.String("A"), Value.String("B")]);
        Task<Value> show = service.ShowSelectionAsync(new object(), request, new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        Click(window, 4097);
        Assert.Equal(expected, (await show.WaitAsync(TimeSpan.FromSeconds(10))).Display());
    }

    [Theory]
    [InlineData(1, ValueKind.Number, "0")]
    [InlineData(65, ValueKind.String, "")]
    [InlineData(129, ValueKind.String, "")]
    public async Task FractionalDeadlineReturnsTheModeSpecificTimeout(int flags, ValueKind kind, string expected)
    {
        await using var service = new NativeDialogService();
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0.15), Value.String("probe")]);
        var timer = Stopwatch.StartNew();
        Value result = await service.ShowSelectionAsync(new object(), request,
            new("uwsc-selection-timer-" + Guid.NewGuid()), 0, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(kind, result.Kind);
        Assert.Equal(expected, result.Display());
        Assert.InRange(timer.Elapsed.TotalMilliseconds, 100, 2000);
    }

    [Theory]
    [InlineData("Choose()", "SLCT_BTN")]
    [InlineData("Choose()", "SLCT_CHK")]
    [InlineData("Choose()", "SLCT_RDO")]
    [InlineData("Choose()", "SLCT_CMB")]
    [InlineData("Choose()", "SLCT_LST")]
    [InlineData("THREAD Choose()", "SLCT_BTN")]
    [InlineData("THREAD Choose()", "SLCT_CHK")]
    [InlineData("THREAD Choose()", "SLCT_RDO")]
    [InlineData("THREAD Choose()", "SLCT_CMB")]
    [InlineData("THREAD Choose()", "SLCT_LST")]
    public async Task ScriptReceivesTheModeSpecificTimeoutValue(
        string call, string mode)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        List<string> lines = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($$"""
            PUBLIC bits=EMPTY
            PUBLIC label=EMPTY
            PUBLIC position=EMPTY
            PUBLIC finished=FALSE
            {{call}}
            WHILE !finished
                SLEEP(0.01)
            WEND
            PRINT VARTYPE(bits)
            PRINT "["+bits+"]"
            PRINT VARTYPE(label)
            PRINT "["+label+"]"
            PRINT VARTYPE(position)
            PRINT "["+position+"]"
            PROCEDURE Choose()
                bits=SLCTBOX({{mode}},0.15,"timeout","A")
                label=SLCTBOX({{mode}} OR SLCT_STR,0.15,"timeout","A")
                position=SLCTBOX({{mode}} OR SLCT_NUM,0.15,"timeout","A")
                finished=TRUE
            FEND
            """, lines.Add, cancellation.Token);

        Assert.Equal(["3", "[0]", "258", "[]", "258", "[]"], lines);
    }

    [Theory]
    [InlineData(1, false, "2", "3")]
    [InlineData(2, false, "2", "3")]
    [InlineData(4, false, "2", "3")]
    [InlineData(8, false, "2", "3")]
    [InlineData(16, false, "2", "3")]
    [InlineData(65, false, "B", "258")]
    [InlineData(66, false, "B", "258")]
    [InlineData(68, false, "B", "258")]
    [InlineData(72, false, "B", "258")]
    [InlineData(80, false, "B", "258")]
    [InlineData(129, false, "1", "3")]
    [InlineData(130, false, "1", "3")]
    [InlineData(132, false, "1", "3")]
    [InlineData(136, false, "1", "3")]
    [InlineData(144, false, "1", "3")]
    [InlineData(65, true, "-1", "3")]
    [InlineData(2, false, "3", "3", true)]
    [InlineData(16, false, "3", "3", true)]
    [InlineData(66, false, "A\tB", "258", true)]
    [InlineData(80, false, "A\tB", "258", true)]
    [InlineData(130, false, "0\t1", "258", true)]
    [InlineData(144, false, "0\t1", "258", true)]
    public async Task ScriptThreadReceivesSelectionOrCancellation(
        int flags, bool close, string expectedValue, string expectedType,
        bool selectBoth = false)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        string title = "uwsc-selection-thread-" + Guid.NewGuid();
        List<string> lines = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $$"""
            PUBLIC selection=EMPTY
            PUBLIC finished=FALSE
            OPTION DLGTITLE="{{title}}"
            THREAD Choose()
            WHILE !finished
                SLEEP(0.01)
            WEND
            PRINT selection
            PRINT VARTYPE(selection)
            PROCEDURE Choose()
                selection=SLCTBOX({{flags}},0,"probe","A","B")
                finished=TRUE
            FEND
            """, lines.Add, cancellation.Token);
        try
        {
            nint window =
                await MessageBoxIntegrationTests.FindWindowAsync(title);
            Assert.False(run.IsCompleted);
            if (close)
            {
                Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            }
            else
            {
                SelectSecondItem(window, flags & 31, selectBoth);
            }

            await run.WaitAsync(cancellation.Token);
            Assert.Equal([expectedValue, expectedType], lines);
            Assert.False(Native.IsWindow(window));
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

    [Theory]
    [InlineData("Choose()", "SLCT_BTN")]
    [InlineData("Choose()", "SLCT_CHK")]
    [InlineData("Choose()", "SLCT_RDO")]
    [InlineData("Choose()", "SLCT_CMB")]
    [InlineData("Choose()", "SLCT_LST")]
    [InlineData("THREAD Choose()", "SLCT_BTN")]
    [InlineData("THREAD Choose()", "SLCT_CHK")]
    [InlineData("THREAD Choose()", "SLCT_RDO")]
    [InlineData("THREAD Choose()", "SLCT_CMB")]
    [InlineData("THREAD Choose()", "SLCT_LST")]
    public async Task ScriptCancellationClosesPendingSelection(
        string call, string mode)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        string title = "uwsc-selection-cancel-" + Guid.NewGuid();
        List<string> lines = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $$"""
            OPTION DLGTITLE="{{title}}"
            {{call}}
            WHILE TRUE
                SLEEP(0.01)
            WEND
            PROCEDURE Choose()
                result=SLCTBOX({{mode}},0,"probe","A","B")
                PRINT result
            FEND
            """, lines.Add, cancellation.Token);
        try
        {
            nint window =
                await MessageBoxIntegrationTests.FindWindowAsync(title);
            Assert.False(run.IsCompleted);
            Assert.False(cancellation.IsCancellationRequested);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(lines);
            Assert.False(Native.IsWindow(window));
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
    public async Task ScriptCancellationClosesBothThreadSelections()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        string title = "uwsc-selection-workers-" + Guid.NewGuid();
        System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $$"""
            OPTION DLGTITLE="{{title}}"
            THREAD Choose(SLCT_BTN)
            THREAD Choose(SLCT_CMB)
            WHILE TRUE
                SLEEP(0.01)
            WEND
            PROCEDURE Choose(mode)
                result=SLCTBOX(mode,0,"probe","A","B")
                PRINT result
            FEND
            """, lines.Enqueue, cancellation.Token);
        try
        {
            List<nint> windows = [];
            var timer = Stopwatch.StartNew();
            do
            {
                windows.Clear();
                Native.EnumWindows((window, _) =>
                {
                    Native.GetWindowThreadProcessId(window, out uint process);
                    if (process == Environment.ProcessId
                        && Native.IsWindowVisible(window)
                        && MessageBoxIntegrationTests.ReadText(window) == title)
                    {
                        windows.Add(window);
                    }

                    return true;
                }, 0);
                if (windows.Count == 2)
                {
                    break;
                }

                await Task.Delay(25, cancellation.Token);
            }
            while (timer.Elapsed < TimeSpan.FromSeconds(10));

            Assert.Equal(2, windows.Count);
            Assert.False(run.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(lines);
            Assert.All(windows, window => Assert.False(Native.IsWindow(window)));
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

    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(129)]
    public async Task ClosingAlwaysReturnsNumericMinusOne(int flags)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-close-" + Guid.NewGuid();
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0), Value.String("probe")]);
        Task<Value> show = service.ShowSelectionAsync(new object(), request, new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        Native.PostMessageW(window, 0x10, 0, 0);
        Assert.Equal(Value.Numeric(-1), await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(129)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(66)]
    [InlineData(68)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(130)]
    [InlineData(132)]
    [InlineData(136)]
    [InlineData(144)]
    [InlineData(8, true)]
    public async Task EscapeClosesSelectionAndReturnsNumericMinusOne(
        int flags, bool showDropdown = false)
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        string title = "uwsc-selection-escape-" + Guid.NewGuid();
        var request = SelectionBoxRequest.Read([Value.Numeric(flags),
            Value.Numeric(0), Value.String("probe"), Value.String("A")]);
        Task<Value> show = service.ShowSelectionAsync(new object(), request,
            new(title), 0, cancellation.Token);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint combo = 0;
        if (showDropdown)
        {
            combo = MessageBoxIntegrationTests.GetDlgItem(window, 201);
            Assert.NotEqual(0, combo);
            Native.SendMessageW(combo, 0x14f, 1, 0);
            Assert.NotEqual(0, Native.SendMessageW(combo, 0x157, 0, 0));
        }

        SendEscape(window);
        if (showDropdown)
        {
            var dropdownClosing = Stopwatch.StartNew();
            while (Native.SendMessageW(combo, 0x157, 0, 0) != 0
                && dropdownClosing.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, cancellation.Token);
            }

            Assert.Equal(0, Native.SendMessageW(combo, 0x157, 0, 0));
            Assert.False(show.IsCompleted);
            SendEscape(window);
        }

        Assert.Equal(Value.Numeric(-1),
            await show.WaitAsync(TimeSpan.FromSeconds(5)));
        var closing = Stopwatch.StartNew();
        while (Native.IsWindow(window)
            && closing.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25, cancellation.Token);
        }

        Assert.False(Native.IsWindow(window));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task KeyboardSelectsAndConfirmsSecondButtonRow(int kind)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-row-keyboard-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", ["A", "B"], null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        SendKey(window, kind == 4 ? (ushort)0x28 : (ushort)0x09);
        if (kind == 2)
        {
            SendKey(window, 0x20);
        }

        SendKey(window, 0x0d);
        Assert.Equal(Value.Numeric(1),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PageKeysReachItemsOutsideTheVisibleRows(int kind)
    {
        const int firstRowId = 0x5000;
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-page-keyboard-" + Guid.NewGuid();
        string[] items = [.. Enumerable.Range(0, 1000).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);

        int rowCount = 0;
        Native.EnumChildWindows(window, (child, _) =>
        {
            int id = Native.GetDlgCtrlID(child);
            if (id >= firstRowId && id < firstRowId + 12)
            {
                rowCount++;
            }

            return true;
        }, 0);
        Assert.InRange(rowCount, 1, 12);
        nint first = MessageBoxIntegrationTests.GetDlgItem(window, firstRowId);
        Assert.Equal(items[0], MessageBoxIntegrationTests.ReadText(first));

        await PageAsync(0x22, items[rowCount]);
        await PageAsync(0x21, items[0]);
        await PageAsync(0x22, items[rowCount]);
        if (kind != 1)
        {
            SendKey(window, 0x20);
        }

        SendKey(window, 0x0d);
        Assert.Equal(Value.Numeric(rowCount),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));

        async Task PageAsync(ushort key, string expected)
        {
            SendKey(window, key);
            var timer = Stopwatch.StartNew();
            string actual = MessageBoxIntegrationTests.ReadText(first);
            while (actual != expected && timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(25);
                actual = MessageBoxIntegrationTests.ReadText(first);
            }

            Assert.Equal(expected, actual);
        }
    }

    private static void SendEscape(nint window) => SendKey(window, 0x1b);

    private static void SendKey(nint window, ushort key)
    {
        Assert.Equal(window, Native.GetForegroundWindow());
        foreach (uint keyFlags in new uint[] { 0, 2 })
        {
            NativeInputSender.Send(new Native.Input
            {
                Type = 1,
                Data = new Native.InputUnion
                {
                    Keyboard = new Native.KeyboardInput
                    {
                        Vk = key,
                        Flags = keyFlags
                    }
                }
            });
        }
    }

    [Theory]
    [InlineData(1, -96, 160)]
    [InlineData(2, 96, -32)]
    [InlineData(4, -96, -32)]
    [InlineData(8, -96, 160)]
    [InlineData(16, 96, -32)]
    public async Task ExplicitNegativePositionCanBeReused(int kind, int x, int y)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using var service = new NativeDialogService();
        object owner = new();
        await CheckAsync(x, y);
        await CheckAsync(-1, -1);

        async Task CheckAsync(int requestedX, int requestedY)
        {
            string title = "uwsc-selection-position-" + Guid.NewGuid();
            Task<Value> show = service.ShowSelectionAsync(owner,
                new(kind, 0, "probe", ["A", "B"], requestedX, requestedY, 128),
                new(title), 0, default);
            nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
            Native.Rect bounds;
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.GetWindowRect(window, out bounds));
            }

            output.WriteLine(
                $"kind={kind} requested={requestedX},{requestedY} actual={bounds.Left},{bounds.Top}");
            Assert.Equal((x, y), (bounds.Left, bounds.Top));
            Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            Assert.Equal(Value.Numeric(-1),
                await show.WaitAsync(TimeSpan.FromSeconds(10)));
            await MessageBoxIntegrationTests.WaitForDestructionAsync(window);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MoreThan31ItemsRemainReachableWithoutCreatingOneWindowPerItem(int kind)
    {
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-page-" + Guid.NewGuid();
        string[] items = [.. Enumerable.Range(0, 1000).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        List<nint> rows = [];
        Native.EnumChildWindows(window, (child, _) =>
        {
            int id = Native.GetDlgCtrlID(child);
            if (id >= 0x5000 && id < 0x5100)
            {
                rows.Add(child);
            }

            return true;
        }, 0);
        Assert.InRange(rows.Count, 1, 12);
        Native.SendMessageTimeoutW(window, 0x115, 7, 0, 2, 1000, out _);
        nint last = MessageBoxIntegrationTests.GetDlgItem(window, 0x5000 + rows.Count - 1);
        Assert.Equal("Item999", MessageBoxIntegrationTests.ReadText(last));
        Click(window, 0x5000 + rows.Count - 1);
        if (kind != 1)
        {
            Click(window, 4097);
        }

        Assert.Equal(Value.Numeric(999),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 1000)]
    [InlineData(4, 1000)]
    [InlineData(1, 65549)]
    public async Task DraggingSelectionScrollbarReachesTheLastItem(
        int kind, int itemCount)
    {
        int last = itemCount - 1;
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-drag-" + Guid.NewGuid();
        string[] items =
            [.. Enumerable.Range(0, itemCount).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        await DragAndClickLastButtonRowAsync(title, items[last], kind != 1);
        Assert.Equal(Value.Numeric(last),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(65_549, "SLCT_BTN", false)]
    [InlineData(UwscArray.MaximumElements, "SLCT_BTN", false)]
    [InlineData(UwscArray.MaximumElements, "SLCT_CHK", true)]
    [InlineData(UwscArray.MaximumElements, "SLCT_RDO", true)]
    public async Task ScriptSelectsTheLastItemInAnArrayBeyond16Bits(
        int count, string mode, bool needsConfirmation)
    {
        int last = count - 1;
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(30));
        string title = "uwsc-selection-array-drag-" + Guid.NewGuid();
        List<string> lines = [];
        Task run = new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $$"""
            OPTION DLGTITLE="{{title}}"
            DIM items[{{last}}]
            items[{{last}}]="最後の項目"
            result=SLCTBOX({{mode}} OR SLCT_NUM,0,"probe",items)
            PRINT result
            PRINT VARTYPE(result)
            """, lines.Add, cancellation.Token);
        try
        {
            await DragAndClickLastButtonRowAsync(
                title, "最後の項目", needsConfirmation);
            await run.WaitAsync(cancellation.Token);
            string expected = last.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal([expected, "3"], lines);
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

    private static async Task DragAndClickLastButtonRowAsync(
        string title, string lastItem, bool needsConfirmation)
    {
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        await DragScrollbarToBottomAsync(window);

        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            for (int row = 0; row < 12; row++)
            {
                int id = 0x5000 + row;
                nint button = MessageBoxIntegrationTests.GetDlgItem(window, id);
                if (button != 0
                    && MessageBoxIntegrationTests.ReadText(button) == lastItem)
                {
                    try
                    {
                        ClickWithMouse(window, button);
                        if (needsConfirmation)
                        {
                            nint confirm =
                                MessageBoxIntegrationTests.GetDlgItem(window, 4097);
                            ClickWithMouse(window, confirm);
                        }
                    }
                    finally
                    {
                        MoveCursor(previous.X, previous.Y);
                    }

                    return;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail("スクロール後に末尾の項目が表示されませんでした。");
    }

    private static async Task DragScrollbarToBottomAsync(nint window)
    {
        var bar = new ScrollBarInfo
        {
            Size = (uint)Marshal.SizeOf<ScrollBarInfo>()
        };
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(GetScrollBarInfo(window, -5, ref bar));
        }

        Assert.True(bar.ThumbBottom > bar.ThumbTop);
        int x = (bar.Bounds.Left + bar.Bounds.Right) / 2;
        int y = bar.Bounds.Top + (bar.ThumbTop + bar.ThumbBottom) / 2;
        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        try
        {
            MoveCursor(x, y);
            SendMouseButton(2);
            try
            {
                await Task.Delay(100);
                MoveCursor(x, bar.Bounds.Bottom);
                await Task.Delay(100);
            }
            finally
            {
                SendMouseButton(4);
            }
        }
        finally
        {
            MoveCursor(previous.X, previous.Y);
        }
    }

    private static void ClickWithMouse(nint window, nint control)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.Equal(window, Native.GetForegroundWindow());
        Assert.NotEqual(0, control);
        Assert.True(Native.GetWindowRect(control, out Native.Rect bounds));
        var point = new Native.Point(
            (bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        Assert.Equal(control, Native.WindowFromPoint(point));
        Assert.True(Native.SetCursorPos(point.X, point.Y));
        SendMouseButton(2);
        SendMouseButton(4);
    }

    private static void MoveCursor(int x, int y)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.SetCursorPos(x, y));
    }

    private static void SendMouseButton(uint flags)
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

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ScrollBarInfo
    {
        public uint Size;
        public Native.Rect Bounds;
        public int LineButton;
        public int ThumbTop;
        public int ThumbBottom;
        public int Reserved;
        public fixed uint State[6];
    }

    [DllImport("user32.dll")]
    private static extern bool GetScrollBarInfo(
        nint window, int objectId, ref ScrollBarInfo info);

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task NativeListScrollbarDragAndClickSelectLastItem(int kind)
    {
        const int last = 999;
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-list-drag-" + Guid.NewGuid();
        string[] items =
            [.. Enumerable.Range(0, last + 1).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint control = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        nint list = control;
        if (kind == 8)
        {
            SendKey(window, 0x73);
            await WaitForDropdownAsync(control, expectedOpen: true);
            var info = new ComboBoxInfo
            {
                Size = (uint)Marshal.SizeOf<ComboBoxInfo>()
            };
            Assert.True(GetComboBoxInfo(control, ref info));
            list = info.List;
        }

        await DragScrollbarToBottomAsync(list);
        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        try
        {
            ClickListItemWithMouse(list, last);
            if (kind == 8)
            {
                await WaitForDropdownAsync(control, expectedOpen: false);
            }

            nint confirm = MessageBoxIntegrationTests.GetDlgItem(window, 4097);
            ClickWithMouse(window, confirm);
        }
        finally
        {
            MoveCursor(previous.X, previous.Y);
        }

        Assert.Equal(Value.Numeric(last),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static unsafe void ClickListItemWithMouse(nint list, int index)
    {
        using var dpi = new PhysicalDpiScope();
        Native.Rect bounds = default;
        Assert.NotEqual(-1,
            Native.SendMessageW(list, 0x198, index, (nint)(&bounds)));
        var point = new Native.Point(
            (bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        Assert.True(Native.ClientToScreen(list, ref point));
        Assert.Equal(list, Native.WindowFromPoint(point));
        Assert.True(Native.SetCursorPos(point.X, point.Y));
        SendMouseButton(2);
        SendMouseButton(4);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public uint Size;
        public Native.Rect Item;
        public Native.Rect Button;
        public uint ButtonState;
        public nint Combo;
        public nint Edit;
        public nint List;
    }

    [DllImport("user32.dll")]
    private static extern bool GetComboBoxInfo(
        nint window, ref ComboBoxInfo info);

    [Theory]
    [InlineData(8, 1000)]
    [InlineData(16, 1000)]
    [InlineData(8, 65549)]
    [InlineData(16, 65549)]
    [InlineData(16, 1_000_000)]
    public async Task LargeNativeSelectionReturnsTheLastItemPosition(
        int kind, int count)
    {
        int last = count - 1;
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        string title = "uwsc-selection-many-items-" + Guid.NewGuid();
        string[] items =
            [.. Enumerable.Range(0, count).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, cancellation.Token);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);
        Assert.Equal((nint)count,
            Native.SendMessageW(list, kind == 8 ? 0x146u : 0x18bu, 0, 0));
        nint selection = kind == 8
            ? Native.SendMessageW(list, 0x14e, last, 0)
            : Native.SendMessageW(list, 0x185, 1, last);
        Assert.Equal(kind == 8 ? (nint)last : 0, selection);
        Click(window, 4097);

        Assert.Equal(Value.Numeric(last),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [Trait("Category", "SandboxOnly")]
    [InlineData(8)]
    public async Task MeasureMillionItemRegistrationBeforeCancellation(
        int kind)
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        string title = "uwsc-selection-registration-time-" + Guid.NewGuid();
        var items = new TimedSelectionItems();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.InRange(items.MeasurementReads, 1, items.Count);
        Assert.InRange(items.RegistrationReads, 1, items.Count);
        output.WriteLine($"kind={kind} measureReads={items.MeasurementReads} "
            + $"registrationStartMs={items.RegistrationStartMs:F0} "
            + $"registrationReads={items.RegistrationReads} "
            + $"registrationElapsedMs={items.RegistrationElapsedMs:F0}");
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task NoDataListBoxAcceptsMillionItemsAndKeepsSelection()
    {
        const int count = 1_000_000;
        const int last = count - 1;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(root =>
            {
                const uint childWithScrollbar = 0x40200000;
                const uint noData = 0x2000;
                const uint ownerDrawFixed = 0x10;
                const uint multipleSelection = 0x08;
                const uint style = childWithScrollbar | noData
                    | ownerDrawFixed | multipleSelection;
                nint list = Native.CreateWindowExW(0,
                    ControlClassNames.Uppercase.ListBox, "",
                    style, 0, 0, 200, 100, root.Root, 201, 0, 0);
                Assert.NotEqual(0, list);

                var timer = Stopwatch.StartNew();
                Assert.True(Native.SendMessageW(list, 0x1a7, count, 0) >= 0);
                output.WriteLine($"setCountMs={timer.Elapsed.TotalMilliseconds:F1}");
                Assert.Equal((nint)count,
                    Native.SendMessageW(list, 0x18b, 0, 0));
                Assert.Equal(0, Native.SendMessageW(list, 0x185, 1, last));
                Assert.Equal(0, Native.SendMessageW(list, 0x185, 1, 0));
                Assert.Equal(1, Native.SendMessageW(list, 0x187, last, 0));
                Assert.Equal(1, Native.SendMessageW(list, 0x187, 0, 0));
                Assert.Equal(2, Native.SendMessageW(list, 0x190, 0, 0));
                nint indices = Marshal.AllocHGlobal(2 * sizeof(int));
                try
                {
                    Assert.Equal(2,
                        Native.SendMessageW(list, 0x191, 2, indices));
                    Assert.Equal(0, Marshal.ReadInt32(indices));
                    Assert.Equal(last,
                        Marshal.ReadInt32(indices, sizeof(int)));
                }
                finally
                {
                    Marshal.FreeHGlobal(indices);
                }

                Assert.Equal(0,
                    Native.SendMessageW(list, 0x19e, last, 0));
                Assert.Equal(last,
                    Native.SendMessageW(list, 0x19f, 0, 0));
            });
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task NoDataListBoxSelectsOneItemOutOfOneMillion()
    {
        const int count = 1_000_000;
        const int last = count - 1;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(root =>
            {
                const uint childWithScrollbar = 0x40200000;
                const uint noData = 0x2000;
                const uint ownerDrawFixed = 0x10;
                nint list = Native.CreateWindowExW(0,
                    ControlClassNames.Uppercase.ListBox, "",
                    childWithScrollbar | noData | ownerDrawFixed,
                    0, 0, 200, 100, root.Root, 201, 0, 0);
                Assert.NotEqual(0, list);
                Assert.True(Native.SendMessageW(list, 0x1a7, count, 0)
                    >= 0);
                Assert.Equal(count,
                    Native.SendMessageW(list, 0x18b, 0, 0));
                Assert.Equal(0,
                    Native.SendMessageW(list, 0x186, 0, 0));
                Assert.Equal(last,
                    Native.SendMessageW(list, 0x186, last, 0));
                Assert.Equal(last,
                    Native.SendMessageW(list, 0x188, 0, 0));
                Assert.Equal(0,
                    Native.SendMessageW(list, 0x187, 0, 0));
            });
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task MeasureComboBoxRegistrationWithoutStrings()
    {
        const int count = 1_000_000;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(root =>
            {
                const uint childWithScrollbar = 0x40200000;
                const uint dropDownList = 0x03;
                const uint ownerDrawFixed = 0x10;
                nint combo = Native.CreateWindowExW(0,
                    ControlClassNames.Uppercase.ComboBox, "",
                    childWithScrollbar | dropDownList | ownerDrawFixed,
                    0, 0, 200, 100, root.Root, 201, 0, 0);
                Assert.NotEqual(0, combo);
                Native.SendMessageW(combo, 0x0b, 0, 0);
                var timer = Stopwatch.StartNew();
                Assert.True(Native.SendMessageW(combo, 0x161, count, 0)
                    >= 0);
                double reserveMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                int added = 0;
                while (added < count && timer.ElapsedMilliseconds < 1500)
                {
                    Assert.Equal(added,
                        Native.SendMessageW(combo, 0x143, 0, added));
                    added++;
                }

                output.WriteLine($"reservedMs={reserveMs:F1} "
                    + $"added={added} elapsedMs={timer.ElapsedMilliseconds}");
                Assert.InRange(added, 1, count);
                Assert.Equal(added, Native.SendMessageW(combo, 0x146, 0, 0));
                Assert.Equal(added - 1,
                    Native.SendMessageW(combo, 0x14e, added - 1, 0));
                Assert.Equal(added - 1,
                    Native.SendMessageW(combo, 0x147, 0, 0));
            });
    }

    private sealed class TimedSelectionItems : IReadOnlyList<string>
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();

        public int Count => 1_000_000;

        internal int MeasurementReads
        {
            get;
            private set;
        }

        internal int RegistrationReads
        {
            get;
            private set;
        }

        internal double RegistrationStartMs
        {
            get;
            private set;
        }

        internal double RegistrationElapsedMs
        {
            get;
            private set;
        }

        public string this[int index]
        {
            get
            {
                // 幅計測とコントロール登録はそれぞれ先頭から読む。
                if (index == 0 && MeasurementReads > 0)
                {
                    Assert.Equal(0, RegistrationReads);
                    RegistrationStartMs = timer.Elapsed.TotalMilliseconds;
                }

                if (RegistrationStartMs > 0)
                {
                    RegistrationReads++;
                    if (RegistrationReads % 32 == 0)
                    {
                        RegistrationElapsedMs =
                            timer.Elapsed.TotalMilliseconds
                            - RegistrationStartMs;
                    }
                }
                else
                {
                    MeasurementReads++;
                }

                return "Item" + index;
            }
        }

        public IEnumerator<string> GetEnumerator() =>
            Enumerable.Range(0, Count).Select(index => this[index])
                .GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable
            .GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(8, true)]
    [InlineData(16, false, 1_000_000)]
    [InlineData(8, false, 1000, true)]
    [InlineData(8, true, 1000, true)]
    [InlineData(16, false, 1000, true)]
    public async Task HomeOrEndAndEnterSelectNativeSelectionItem(
        int kind, bool openDropdown = false, int count = 1000,
        bool returnToFirst = false)
    {
        int last = count - 1;
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-keyboard-" + Guid.NewGuid();
        string[] items =
            [.. Enumerable.Range(0, last + 1).Select(index => "Item" + index)];
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);
        if (openDropdown)
        {
            SendKey(window, 0x73);
            await WaitForDropdownAsync(list, expectedOpen: true);
        }

        SendKey(window, 0x23);
        uint indexMessage = kind == 8 ? 0x147u : 0x19fu;
        var timer = Stopwatch.StartNew();
        while (Native.SendMessageW(list, indexMessage, 0, 0) != last
            && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.Equal((nint)last,
            Native.SendMessageW(list, indexMessage, 0, 0));
        int expected = returnToFirst ? 0 : last;
        if (returnToFirst)
        {
            SendKey(window, 0x24);
            timer.Restart();
            while (Native.SendMessageW(list, indexMessage, 0, 0) != expected
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal((nint)expected,
                Native.SendMessageW(list, indexMessage, 0, 0));
        }

        if (kind == 16
            && Native.SendMessageW(list, 0x187, expected, 0) == 0)
        {
            SendKey(window, 0x20);
            timer.Restart();
            while (Native.SendMessageW(list, 0x187, expected, 0) == 0
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal((nint)1,
                Native.SendMessageW(list, 0x187, expected, 0));
        }

        SendKey(window, 0x0d);
        if (openDropdown)
        {
            await WaitForDropdownAsync(list, expectedOpen: false);
            Assert.False(show.IsCompleted);
            Assert.Equal((nint)expected, Native.SendMessageW(list, 0x147, 0, 0));
            SendKey(window, 0x0d);
        }

        Assert.Equal(Value.Numeric(expected),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(0x0d, 999_999)]
    [InlineData(0x1b, 0)]
    [InlineData(0x09, 0)]
    [InlineData(0, 0)]
    public async Task MillionItemComboConfirmsOrRestoresSelection(
        int closeKey, int expected)
    {
        const int last = 999_999;
        string[] items = new string[last + 1];
        Array.Fill(items, "Item");
        await using var service = new NativeDialogService();
        string title = "uwsc-large-combo-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(8, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);
        SendKey(window, 0x73);
        await WaitForListVisibilityAsync(true);
        SendKey(window, 0x23);
        var timer = Stopwatch.StartNew();
        while (Native.SendMessageW(list, 0x188, 0, 0) != last
            && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.Equal(last, Native.SendMessageW(list, 0x188, 0, 0));
        if (closeKey == 0)
        {
            Task<int> other = service.ShowMessageAsync(new object(),
                new("probe", 1, null, null, 0, true),
                new(title + "-other"), 0, default);
            nint otherWindow =
                await MessageBoxIntegrationTests.FindWindowAsync(
                    title + "-other");
            await UiInputDriver.ActivateAsync(otherWindow);
            await WaitForListVisibilityAsync(false);
            Assert.Equal(otherWindow, Native.GetForegroundWindow());
            Assert.True(Native.PostMessageW(otherWindow, 0x10, 0, 0));
            await other.WaitAsync(TimeSpan.FromSeconds(5));
            await UiInputDriver.ActivateAsync(window);
        }
        else
        {
            SendKey(window, (ushort)closeKey);
        }
        await WaitForListVisibilityAsync(false);
        Assert.False(show.IsCompleted);
        if (closeKey == 0x09)
        {
            Click(window, 4097);
        }
        else
        {
            SendKey(window, 0x0d);
        }
        Assert.Equal(Value.Numeric(expected),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));

        async Task WaitForListVisibilityAsync(bool visible)
        {
            var timer = Stopwatch.StartNew();
            while (Native.IsWindowVisible(list) != visible
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal(visible, Native.IsWindowVisible(list));
        }
    }

    [Fact]
    public async Task LargeComboFitsBelowScreenEdgeAndRestoresPosition()
    {
        var monitor = new Native.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
        };
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetMonitorInfoW(
                Native.MonitorFromPoint(new Native.Point(0, 0), 2),
                ref monitor));
        }

        string[] items = new string[100_000];
        Array.Fill(items, "Item");
        await using var service = new NativeDialogService();
        string title = "uwsc-large-combo-bottom-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(8, 0, "probe", items, monitor.Work.Left + 20,
                monitor.Work.Bottom - 80, 128), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        Native.Rect closed = ReadBounds(window);
        await UiInputDriver.ActivateAsync(window);
        SendKey(window, 0x73);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        await WaitForVisibilityAsync(true);
        Native.Rect opened = ReadBounds(window);
        Assert.True(opened.Top < closed.Top);
        Assert.InRange(opened.Top, monitor.Work.Top, monitor.Work.Bottom);
        Assert.InRange(opened.Bottom, opened.Top, monitor.Work.Bottom);
        SendKey(window, 0x1b);
        await WaitForVisibilityAsync(false);
        var restore = Stopwatch.StartNew();
        while (!ReadBounds(window).Equals(closed)
            && restore.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.Equal(closed, ReadBounds(window));
        SendKey(window, 0x0d);
        Assert.Equal(Value.Numeric(0),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));

        async Task WaitForVisibilityAsync(bool visible)
        {
            var timer = Stopwatch.StartNew();
            while (Native.IsWindowVisible(list) != visible
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal(visible, Native.IsWindowVisible(list));
        }

        static Native.Rect ReadBounds(nint handle)
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetWindowRect(handle, out Native.Rect rect));
            return rect;
        }
    }

    [Fact]
    public async Task LargeComboCommitsLastItemWithOneMouseClick()
    {
        const int last = 99_999;
        string[] items = new string[last + 1];
        Array.Fill(items, "Item");
        await using var service = new NativeDialogService();
        string title = "uwsc-large-combo-mouse-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(8, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        nint button = MessageBoxIntegrationTests.GetDlgItem(window, 203);
        Assert.NotEqual(0, list);
        Assert.NotEqual(0, button);
        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        try
        {
            ClickWithMouse(window, button);
            await WaitForListVisibilityAsync(true);
            Assert.Equal(0, Native.SendMessageW(list, 0x197, last, 0));
            ClickListItemWithMouse(list, last);
            await WaitForListVisibilityAsync(false);
            Assert.False(show.IsCompleted);
            ClickWithMouse(window,
                MessageBoxIntegrationTests.GetDlgItem(window, 4097));
            Assert.Equal(Value.Numeric(last),
                await show.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            MoveCursor(previous.X, previous.Y);
        }

        async Task WaitForListVisibilityAsync(bool visible)
        {
            var timer = Stopwatch.StartNew();
            while (Native.IsWindowVisible(list) != visible
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal(visible, Native.IsWindowVisible(list));
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public async Task LargeComboTimesOutWithItsListOpen(int returnMode)
    {
        string[] items = new string[100_000];
        Array.Fill(items, "Item");
        await using var service = new NativeDialogService();
        string title = "uwsc-large-combo-timeout-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(8, 4, "probe", items, null, null, returnMode),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);
        SendKey(window, 0x73);

        var timer = Stopwatch.StartNew();
        while (!Native.IsWindowVisible(list) && !show.IsCompleted
            && timer.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(25);
        }

        Assert.True(Native.IsWindowVisible(list));
        Assert.Equal(Value.String(""),
            await show.WaitAsync(TimeSpan.FromSeconds(8)));
        timer.Restart();
        while (Native.IsWindow(window)
            && timer.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(25);
        }

        Assert.False(Native.IsWindow(list));
        Assert.False(Native.IsWindow(window));
    }

    [Fact]
    public async Task CharacterKeyWrapsInMillionItemList()
    {
        const int last = 999_999;
        string[] items = new string[last + 1];
        Array.Fill(items, "Item");
        items[1] = "z first";
        items[last] = "Z last";
        await using var service = new NativeDialogService();
        string title = "uwsc-selection-character-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(16, 0, "probe", items, null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);

        await MoveToAsync(1);
        await MoveToAsync(last);
        await MoveToAsync(1);
        Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
        Assert.Equal(Value.Numeric(-1),
            await show.WaitAsync(TimeSpan.FromSeconds(10)));

        async Task MoveToAsync(int expected)
        {
            SendKey(window, 0x5a);
            var timer = Stopwatch.StartNew();
            while (Native.SendMessageW(list, 0x19f, 0, 0) != expected
                && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.Equal((nint)expected,
                Native.SendMessageW(list, 0x19f, 0, 0));
        }
    }

    [Fact]
    public async Task MillionItemListDrawsLastItemAfterScrolling()
    {
        const int last = 999_999;
        string[] items = new string[last + 1];
        Array.Fill(items, "");
        items[last] = "LAST ITEM";
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        string title = "uwsc-selection-drawing-" + Guid.NewGuid();
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(16, 0, "probe", items, null, null, 128),
            new(title), 0, cancellation.Token);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        await UiInputDriver.ActivateAsync(window);
        nint list = MessageBoxIntegrationTests.GetDlgItem(window, 201);
        Assert.NotEqual(0, list);
        Assert.Equal(0, Native.SendMessageW(list, 0x197, last, 0));

        var timer = Stopwatch.StartNew();
        int colors;
        do
        {
            colors = ReadItemInterior(list, last).Pixels
                .Select(pixel => pixel & 0xffffff).Distinct().Count();
            if (colors > 1)
            {
                break;
            }

            await Task.Delay(25, cancellation.Token);
        }
        while (timer.Elapsed < TimeSpan.FromSeconds(5));

        Assert.True(colors > 1);
        Assert.Single(ReadItemInterior(list, last - 1).Pixels
            .Select(pixel => pixel & 0xffffff).Distinct());
        Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
        Assert.Equal(Value.Numeric(-1),
            await show.WaitAsync(cancellation.Token));
    }

    private static unsafe PixelImage ReadItemInterior(nint list, int index)
    {
        using var dpi = new PhysicalDpiScope();
        Native.Rect bounds = default;
        Assert.NotEqual(-1,
            Native.SendMessageW(list, 0x198, index, (nint)(&bounds)));
        Assert.True(Native.GetClientRect(list, out Native.Rect client));
        Assert.InRange(bounds.Top, 0, client.Bottom - 1);
        Assert.InRange(bounds.Bottom, bounds.Top + 1, client.Bottom);
        var point = new Native.Point(bounds.Left + 3, bounds.Top + 3);
        Assert.True(Native.ClientToScreen(list, ref point));
        return new NativeImageCapture().Capture(
            new(point.X, point.Y, 150, bounds.Bottom - bounds.Top - 6),
            default);
    }

    private static async Task WaitForDropdownAsync(nint combo, bool expectedOpen)
    {
        var timer = Stopwatch.StartNew();
        while ((Native.SendMessageW(combo, 0x157, 0, 0) != 0) != expectedOpen
            && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.True(Native.IsWindow(combo));
        Assert.Equal(expectedOpen, Native.SendMessageW(combo, 0x157, 0, 0) != 0);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public async Task CancellationDuringItemPreparationDestroysWindow(
        int kind, bool measuring)
    {
        await using var service = new NativeDialogService();
        using var cancellation = new CancellationTokenSource();
        string title = "uwsc-selection-prepare-" + Guid.NewGuid();
        var items = new CancelingSelectionItems(
            title, cancellation, measuring);
        Task<Value> show = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", items, null, null, 128),
            new(title), 0, cancellation.Token);

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotEqual(0, items.Window);
        Assert.Equal(measuring, items.Control == 0);
        Assert.False(Native.IsWindow(items.Window));
        if (!measuring)
        {
            Assert.False(Native.IsWindow(items.Control));
        }

        Task<Value> next = service.ShowSelectionAsync(new object(),
            new(kind, 0, "probe", ["A"], null, null, 128),
            new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
        Assert.Equal(Value.Numeric(-1),
            await next.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class CancelingSelectionItems(
        string title, CancellationTokenSource cancellation, bool measuring)
        : IReadOnlyList<string>
    {
        public int Count => 1000;

        internal nint Window
        {
            get;
            private set;
        }

        internal nint Control
        {
            get;
            private set;
        }

        public string this[int index]
        {
            get
            {
                if (index == 31)
                {
                    uint thread = Native.GetCurrentThreadId();
                    Native.EnumWindows((window, unused) =>
                    {
                        if (Native.GetWindowThreadProcessId(window, out _)
                            != thread
                            || MessageBoxIntegrationTests.ReadText(window)
                                != title)
                        {
                            return true;
                        }

                        Window = window;
                        Control = MessageBoxIntegrationTests.GetDlgItem(
                            window, 201);
                        return false;
                    }, 0);
                    if (Window != 0 && (Control == 0) == measuring)
                    {
                        cancellation.Cancel();
                    }
                }

                Assert.InRange(index, 0,
                    cancellation.IsCancellationRequested ? 31 : Count - 1);
                return "Item" + index;
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task SessionCancellationReleasesItsSelectionWindow()
    {
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        string title = "uwsc-selection-session-" + Guid.NewGuid();
        session.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        using var cancellation = new CancellationTokenSource();
        Task<Value> show = session.InvokeAsync(BuiltinFunctionNames.SlctBox,
            [Value.Numeric(1), Value.Numeric(0), Value.String("probe")], cancellation.Token).AsTask();
        await MessageBoxIntegrationTests.FindWindowAsync(title);
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            show.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task DeadlineExpiresOnTheFirstTimerAfterAUiStall()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var service = new NativeDialogService(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        });
        string title = "uwsc-selection-stall-" + Guid.NewGuid();
        const string address = "https://example.invalid/";
        Task<int> message = service.ShowMessageAsync(new object(),
            new(address, 1, null, null, 0, true), new(title + "-message"), 0, default);
        nint messageWindow = await MessageBoxIntegrationTests.FindWindowAsync(title + "-message");
        Task<Value> selection = service.ShowSelectionAsync(new object(),
            new(1, 1, "probe", ["A"], null, null, 0), new(title), 0, default);
        await MessageBoxIntegrationTests.FindWindowAsync(title);
        Task sending = Task.Factory.StartNew(() =>
        {
            nint buffer = Marshal.AllocHGlobal(52);
            try
            {
                Marshal.Copy(new byte[52], 0, buffer, 52);
                Marshal.WriteIntPtr(buffer, 0, MessageBoxIntegrationTests.GetDlgItem(messageWindow, 200));
                Marshal.WriteIntPtr(buffer, 8, 200);
                Marshal.WriteInt32(buffer, 16, 0x70b);
                Marshal.WriteInt32(buffer, 24, 0x202);
                Marshal.WriteInt32(buffer, 48, address.Length);
                Assert.NotEqual(0, Native.SendMessageTimeoutW(messageWindow, 0x4e, 200,
                    buffer, 2, 5000, out _));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            await Task.Delay(1500);
            Assert.False(selection.IsCompleted);
            var afterRelease = Stopwatch.StartNew();
            release.Set();
            Assert.Equal(Value.Numeric(0), await selection.WaitAsync(TimeSpan.FromSeconds(2)));
            output.WriteLine($"after_stall_ms={afterRelease.Elapsed.TotalMilliseconds:0.0}");
            Assert.True(afterRelease.Elapsed < TimeSpan.FromMilliseconds(600),
                $"期限切れの反映: {afterRelease.Elapsed.TotalMilliseconds:0}ms");
            await sending.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.Set();
            Native.PostMessageW(messageWindow, 0x10, 0, 0);
        }

        await message.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RootDisposalCancelsForkSelectionBeforeWaitingForItsRelease()
    {
        await using IWindowsAutomationSession root = new WindowsAutomation().CreateSession();
        IWindowsAutomationSession child = root.Fork();
        string title = "uwsc-selection-fork-" + Guid.NewGuid();
        child.ConfigureDialogOption(Keywords.DlgTitle, [Value.String(title)], default);
        Task<Value> show = child.InvokeAsync(BuiltinFunctionNames.SlctBox,
            [Value.Numeric(1), Value.Numeric(0), Value.String("probe")], default).AsTask();
        await MessageBoxIntegrationTests.FindWindowAsync(title);
        Task disposing = root.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => show.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await child.DisposeAsync();
        }

        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Click(nint window, int id)
    {
        nint control = MessageBoxIntegrationTests.GetDlgItem(window, id);
        Assert.NotEqual(0, control);
        Assert.NotEqual(0, Native.SendMessageTimeoutW(control, 0xf5, 0, 0, 2, 1000, out _));
    }
}
