using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class ListControlTests(ITestOutputHelper output)
{
    private static Value N(int value) => Value.Numeric(value);

    private static Value S(string value) => Value.String(value);

    private static nint Message(nint target, uint message, nint value = 0)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, message,
            value, 0, 2, 1000, out nuint result));
        return unchecked((nint)result);
    }

    private static void Add(nint target, bool combo, params string[] items)
    {
        foreach (string item in items)
        {
            Assert.NotEqual(0, Native.SendText(target, combo ? 0x143u : 0x180u,
                0, item, 2, 1000, out nuint result));
            Assert.True(unchecked((nint)result) >= 0);
        }
    }

    [Fact]
    public async Task EnumeratesStringsAndPublishesImmutableSessionSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 1, 10);
        nint combo = fixture.Child("COMBOBOX", "", 3, 60, height: 80);
        nint disabled = fixture.Child("LISTBOX", "", 0x08000000, 100);
        nint hidden = fixture.Child("LISTBOX", "", 0, 130);
        nint multiple = fixture.Child("LISTBOX", "", 8, 160);
        Native.ShowWindow(hidden, 0);
        string longText = new('長', 4097);
        Add(list, false, " first ", "", " \t ", "日本語", "日本語", longText);
        Add(combo, true, " combo ", "last");
        Add(disabled, false, "disabled");
        Add(hidden, false, "hidden");
        Add(multiple, false, "multi-1", "multi-2");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(4), await session.InvokeAsync("GETITEM", [id, N(2)], default));
        Assert.Equal(["first", "日本語", "日本語", longText], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot)[0] = "changed");
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(2), N(2)], default));
        Assert.Equal(["combo", "last"], session.AllItems);
        Assert.Equal("first", snapshot[0]);
        Assert.Equal(N(9), await session.InvokeAsync("GETITEM",
            [id, N(2), N(-1)], default));
        Assert.Equal(["first", "日本語", "日本語", longText, "combo", "last",
            "disabled", "multi-1", "multi-2"], session.AllItems);
        Assert.Equal(N(8), await session.InvokeAsync("GETITEM",
            [id, N(2), N(-1), N(0), N(1)], default));
        Assert.DoesNotContain("disabled", session.AllItems);
        snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(2)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        nint itemData = fixture.Child("LISTBOX", "", 0x10, 180);
        Assert.Equal((nint)0, Native.SendMessageW(itemData, 0x180, 0, 0x00420041));
        Assert.Equal(N(10), await session.InvokeAsync("GETITEM", [id, N(2), N(-1)], default));
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Equal("ABlti-2", session.AllItems[^1]);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM",
            [id, N(2), N(999)], default));
        Assert.Empty(session.AllItems);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [N(-1), N(2)], default));
        await using IWindowsAutomationSession other = fixture.Session();
        Assert.Empty(other.AllItems);
        Assert.Equal(N(0), await other.InvokeAsync("GETITEM", [id, N(2)], default));
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(2)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, S("first")], default));
        await session.DisposeAsync();
        Assert.Empty(session.AllItems);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(2)], default));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public Task SelectsWithNeighborNotificationsEvenWhenAlreadySelected(bool combo, int style) =>
        VerifyNeighborNotifications(combo, style, null);

    /// <summary>
    /// 隔離probeから、選択設定直後のゲスト内入力を再現します。
    /// </summary>
    internal Task InvestigateNeighborNotifications(Action<nint> afterSelection) =>
        VerifyNeighborNotifications(true, 2, afterSelection);

    private async Task VerifyNeighborNotifications(bool combo, int style,
        Action<nint>? afterSelection)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture(avoidPointer: combo);
        nint list = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", style,
            10, 64, 100);
        Add(list, combo, " alpha ", "日本語", "dup", "dup", "A&B", " 保存(&S):... ");
        uint get = combo ? 0x147u : 0x188u;
        uint set = combo ? 0x14eu : 0x186u;
        List<(nint Sender, int Id, int Code, nint Selection)> notifications = [];
        var platform = new SelectionTracePlatform { AfterSelection = afterSelection };
        fixture.Command = (sender, command) =>
        {
            if ((command >> 16) == 1)
            {
                notifications.Add((sender, command & 0xffff, command >> 16, Message(sender, get)));
                platform.Events.Add($"notify:{notifications[^1]}");
            }
        };
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        try
        {
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, S("ALPHA"), N(2 | 512 | 536870912)], default));
        }
        finally
        {
            output.WriteLine(string.Join("\n", platform.Events));
        }
        Assert.Equal(0, Message(list, get));
        bool notify = combo || style == 1;
        if (combo)
        {
            Assert.Equal(style == 1 ? 1 : 0, Message(list, 0x157));
        }
        VerifySelectionNotifications();

        platform.Reset();
        notifications.Clear();
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("alpha"), N(2)], default));
        output.WriteLine(string.Join("\n", platform.Events));
        VerifySelectionNotifications();
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("A&B"), N(2)], default));
        Assert.Equal(4, Message(list, get));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("保存"), N(2 | 536870912)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("保存(&S):..."), N(2)], default));
        Assert.Equal(5, Message(list, get));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("dup"), N(2)], default));
        Assert.Equal(2, Message(list, get));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("missing"), N(2 | 536870912)], default));
        Assert.Equal(2, Message(list, get));
        if (notify)
        {
            fixture.Command = (sender, _) => Message(sender, set, 0);
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
                [id, S("日本語"), N(2)], default));
            Assert.Equal(0, Message(list, get));
            if (combo)
            {
                Assert.Equal(style == 1 ? 1 : 0, Message(list, 0x157));
            }
            using var cancellation = new CancellationTokenSource();
            fixture.Command = (_, _) => cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM",
                    [id, S("日本語"), N(2)], cancellation.Token));
            Assert.Equal(0, Message(list, get));
            if (combo)
            {
                Assert.Equal(style == 1 ? 1 : 0, Message(list, 0x157));
            }
        }

        Assert.Null(fixture.CallbackFailure);

        void VerifySelectionNotifications()
        {
            // 各再試行の通知と確認結果を照合し、余分な通知は許容しません。
            Assert.InRange(platform.SelectionSets, 1, 3);
            Assert.Equal(platform.SelectionSets, platform.SelectionReads.Count);
            Assert.All(platform.SelectionReads.SkipLast(1),
                selected => Assert.NotEqual(0, selected));
            Assert.Equal(0, platform.SelectionReads[^1]);
            (nint, int, int, nint)[] pair =
                [(list, 64, 1, (nint)1), (list, 64, 1, (nint)0)];
            Assert.Equal(notify
                ? Enumerable.Range(0, platform.SelectionSets).SelectMany(_ => pair)
                : [], notifications);
        }
    }

    private sealed class SelectionTracePlatform : OwnedListSelectionPlatform
    {
        internal Action<nint>? AfterSelection
        {
            get; init;
        }

        internal List<string> Events { get; } = [];

        internal bool RejectFirstSelection
        {
            get; init;
        }

        internal int SelectionSets
        {
            get; private set;
        }

        internal List<nint> SelectionReads { get; } = [];

        internal void Reset()
        {
            Events.Clear();
            SelectionSets = 0;
            SelectionReads.Clear();
        }

        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            Events.Add($"send:{window}:{message:x}:{parameter}");
            nint result = base.Send(window, message, parameter, data,
                timed, timeout, flags);
            if (message is 0x14e or 0x186)
            {
                SelectionSets++;
                if (SelectionSets == 1)
                {
                    AfterSelection?.Invoke(window);
                }
            }

            if (message is 0x147 or 0x188)
            {
                SelectionReads.Add(result);
                if (RejectFirstSelection && SelectionReads.Count == 1)
                {
                    Events.Add($"injected-mismatch:actual={result}");
                    result = -1;
                }
            }

            Events.Add($"result:{message:x}:{result}");
            return result;
        }
    }

    [Fact]
    public async Task TransientSelectionMismatchRepeatsNeighborNotifications()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint combo = fixture.Child("COMBOBOX", "", 3, 10, 64, 100);
        Add(combo, true, "alpha", "second");
        var platform = new SelectionTracePlatform { RejectFirstSelection = true };
        List<nint> notifications = [];
        fixture.Command = (sender, command) =>
        {
            if (sender == combo && (command >> 16) == 1)
            {
                notifications.Add(Message(combo, 0x147));
                platform.Events.Add($"notify:{notifications[^1]}");
            }
        };
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        try
        {
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, S("alpha"), N(2 | 536870912)], default));
            Assert.Equal(2, platform.SelectionSets);
            Assert.Equal([(nint)0, (nint)0], platform.SelectionReads);
            Assert.Equal([(nint)1, (nint)0, (nint)1, (nint)0], notifications);
            Assert.Equal(0, Message(combo, 0x147));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            output.WriteLine(string.Join("\n", platform.Events));
        }
    }

    [Fact]
    public async Task NumberSelectsControlAndDefaultPrioritizesButton()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("LISTBOX", "", 1, 10);
        nint second = fixture.Child("COMBOBOX", "", 3, 50);
        nint disabled = fixture.Child("LISTBOX", "", 0x08000000, 80);
        nint multiple = fixture.Child("LISTBOX", "", 8, 110);
        nint button = fixture.Child("BUTTON", "target", 3, 140);
        Add(first, false, "first", "target");
        Add(second, true, "second", "target");
        Add(disabled, false, "disabled");
        Add(multiple, false, "target");
        List<nint> clickedButtons = [];
        fixture.Clicked = (sender, _) => clickedButtons.Add(sender);
        // 状態確認は所有BUTTONへ配送した後に行い、製品のpost完了は仮定しません。
        await using IWindowsAutomationSession session =
            fixture.Session(new OwnedButtonMessagePlatform());
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, S("target")], default));
        Assert.Equal([button], clickedButtons);
        Assert.Equal(1, Message(button, 0xf0));
        Assert.Equal(-1, Message(first, 0x188));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("second"), N(2)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("second"), N(2 | 536870912), N(1), N(1)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("second"), N(2), N(1), N(2)], default));
        Assert.Equal(0, Message(second, 0x147));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("disabled"), N(2 | 536870912)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, S("first")], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("target"), N(2), N(1), N(3)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("first\t target"), N(2 | 536870912)], default));
    }

    [Fact]
    public async Task DefaultButtonMatchReturnsBeforeQueuedClickIsDelivered()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 1, 10);
        nint button = fixture.Child("BUTTON", "target", 3, 60);
        Add(list, false, "target");
        var platform = new QueuedButtonPlatform(button);
        await using IWindowsAutomationSession session = fixture.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);

        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("target")], default));
        Assert.Equal([0x201u, 0x202u], platform.PendingMessages);
        Assert.Equal(0, Message(button, 0xf0));
        Assert.Equal(-1, Message(list, 0x188));

        platform.Deliver();

        Assert.Equal(1, Message(button, 0xf0));
        Assert.Equal(-1, Message(list, 0x188));
        Assert.Null(fixture.CallbackFailure);
    }

    private sealed class QueuedButtonPlatform(nint button)
        : OwnedListSelectionPlatform
    {
        private readonly List<(uint Message, nint Parameter, nint Data)> pending = [];

        internal IEnumerable<uint> PendingMessages =>
            pending.Select(message => message.Message);

        public override void Post(nint window, uint message,
            nint parameter, nint data)
        {
            Assert.Equal(button, window);
            pending.Add((message, parameter, data));
        }

        internal void Deliver()
        {
            foreach ((uint message, nint parameter, nint data) in pending)
            {
                Native.SendMessageW(button, message, parameter, data);
            }

            pending.Clear();
        }
    }

    [Fact]
    public async Task GetItemAndClickListShareNumericControlOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 1, 10);
        nint combo = fixture.Child("COMBOBOX", "", 3, 10);
        Add(list, false, "list", "common");
        Add(combo, true, "combo", "common");
        List<nint> order = [];
        Native.EnumChildWindows(fixture.Root, (child, _) =>
        {
            if (child == list || child == combo)
            {
                order.Add(child);
            }

            return true;
        }, 0);
        Assert.Equal(2, order.Count);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM", [id, N(2)], default));
        Assert.Equal(order[1] == list ? "list" : "combo", session.AllItems[0]);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("common"), N(2)], default));
        Assert.Equal(1, Message(order[1], order[1] == list ? 0x188u : 0x147u));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyStringlessOwnerDrawHasNoItemsToReadOrSelect(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 0x13 : 0x10, 10);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(2)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, S("data"), N(2 | 536870912)], default));
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Empty(session.AllItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsListItemsByWindowTitle(bool empty)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "UWSCSharp-Form1-" + Guid.NewGuid();
        using var fixture = new ButtonControlTests.Fixture(title);
        nint list = fixture.Child("LISTBOX", "", 0, 10);
        if (!empty)
        {
            Add(list, false, "日本語", "second");
        }

        string source = $$"""
            id = GETID("{{title}}")
            FOR item IN GETITEM(id, ITM_LIST)
                PRINT item
            NEXT
            """;
        List<string> output = [];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")));
        await runner.RunAsync(source, output.Add, cancellation.Token);
        Assert.Equal(empty ? [] : new[] { "日本語", "second" }, output);
        output.Clear();
        await runner.RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            FOR item IN GETITEM(id, ITM_LIST)
                PRINT item
                GETITEM(-1, ITM_LIST)
            NEXT
            PRINT LENGTH(ALL_ITEM_LIST)
            """, output.Add, cancellation.Token);
        Assert.Equal(empty ? new[] { "0" } : ["日本語", "second", "0"], output);
    }
}
