using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class WindowsListTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task AcceptsListZeroAndNegativeNumberBeforeAdapter(int number)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT CLKITEM(1, \"x\", CLK_LIST, TRUE, {number})", output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(["CLKITEM"], Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("CLK_LIST OR CLK_MOUSEMOVE")]
    [InlineData("CLK_LIST OR CLK_MOUSEMOVE OR CLK_BACK")]
    [InlineData("CLK_LIST OR CLK_SHORT")]
    [InlineData("CLK_LIST OR CLK_SHORT OR CLK_BACK")]
    [InlineData("CLK_LIST OR CLK_OFTHIS")]
    [InlineData("CLK_LIST OR CLK_SHORT OR CLK_OFTHIS")]
    [InlineData("CLK_LIST OR CLK_UIA OR CLK_OFTHIS")]
    [InlineData("CLK_LIST OR CLK_UIA OR CLK_SHORT OR CLK_OFTHIS")]
    public async Task AcceptsListMoveAndShortFlagsBeforeAdapter(string flags)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT CLKITEM(1, \"x\", " + flags + ")", output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(["CLKITEM"], Assert.Single(automation.Sessions).Calls);
    }

    private sealed class Automation : IWindowsAutomation
    {
        internal List<Session> Sessions { get; } = [];

        public IWindowsAutomationSession CreateSession()
        {
            var session = new Session();
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class Session : IWindowsAutomationSession
    {
        public IReadOnlyList<string> AllItems { get; private set; } = [];

        internal List<string> Calls { get; } = [];

        internal bool Disposed
        {
            get; private set;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(name);
            if (name == "GETSLCTLST")
            {
                return ValueTask.FromResult(Value.String("日本語\tsecond"));
            }

            if (name == "GETITEM")
            {
                if (arguments[0].AsNumber() is 3 or 4)
                {
                    AllItems = arguments[0].AsNumber() == 3 ? ["partial"] : [];
                    throw new InvalidOperationException("取得途中の失敗");
                }

                AllItems = arguments[0].AsNumber() == 1
                    ? ["日本語", "second"] : ["nested"];
                return ValueTask.FromResult(Value.Numeric(AllItems.Count));
            }

            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task PublishesArrayAndForInKeepsItsSnapshot()
    {
        var automation = new Automation();
        var runner = new Interpreter(windows: automation);
        List<string> output = [];
        await runner.RunAsync("""
            PRINT GETITEM(1, ITM_LIST)
            snapshot = ALL_ITEM_LIST
            FOR item IN GETITEM(1, ITM_LIST, -1)
                PRINT item
                GETITEM(2, ITM_LIST)
            NEXT
            PRINT LENGTH(ALL_ITEM_LIST)
            PRINT snapshot[0]
            CLKITEM(1, "日本語", CLK_LIST OR CLK_API OR CLK_BACK)
            """, output.Add);
        Assert.Equal(["2", "日本語", "second", "1", "日本語"], output);
        Session session = Assert.Single(automation.Sessions);
        Assert.Equal(["GETITEM", "GETITEM", "GETITEM", "GETITEM", "CLKITEM"],
            session.Calls);
        Assert.True(session.Disposed);
        await Assert.ThrowsAsync<ScriptException>(() => runner.RunAsync(
            "PRINT ALL_ITEM_LIST[0]", _ => { }));
        Assert.Equal(2, automation.Sessions.Count);
        Assert.Empty(automation.Sessions[1].Calls);
    }

    [Fact]
    public async Task ProcedurePublishesGlobalAndClearsLocalShadow()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PRINT load()
            PRINT ALL_ITEM_LIST[1]
            FUNCTION load()
                ALL_ITEM_LIST = "local shadow"
                GETITEM(1, ITM_LIST)
                RESULT = ALL_ITEM_LIST[0]
            FEND
            """, output.Add);
        Assert.Equal(["日本語", "second"], output);
    }

    [Theory]
    [InlineData(3, "1", "partial")]
    [InlineData(4, "0", "empty")]
    public async Task FailedAdapterPublishesFinalItemsAndPreservesSavedArray(
        int id, string count, string first)
    {
        var automation = new Automation();
        List<string> output = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Interpreter(windows: automation)
            .RunAsync($"""
                GETITEM(1, ITM_LIST)
                PUBLIC saved = 0
                saved = ALL_ITEM_LIST
                fail()
                PROCEDURE fail()
                    ALL_ITEM_LIST = "shadow"
                    TRY
                        GETITEM({id}, ITM_LIST)
                    FINALLY
                        PRINT LENGTH(ALL_ITEM_LIST)
                        IF LENGTH(ALL_ITEM_LIST) > 0
                            PRINT ALL_ITEM_LIST[0]
                        ELSE
                            PRINT "empty"
                        ENDIF
                        PRINT saved[0]
                    ENDTRY
                FEND
                """, output.Add));
        Assert.Equal([count, first, "日本語"], output);
        Assert.Equal(["GETITEM", "GETITEM"], Assert.Single(automation.Sessions).Calls);
    }

    [Fact]
    public async Task ValidationFailureDoesNotReplacePublishedItems()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            GETITEM(1, ITM_LIST)
            TRY
                GETITEM(3, 0)
            EXCEPT
                PRINT ALL_ITEM_LIST[0]
            ENDTRY
            """, output.Add);
        Assert.Equal(["日本語"], output);
        Assert.Equal(["GETITEM"], Assert.Single(automation.Sessions).Calls);
    }

    [Fact]
    public async Task SelectedListReturnsTextWithoutOverwritingItems()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            GETITEM(1, ITM_LIST)
            PRINT GETSLCTLST(1, , 0)
            PRINT ALL_ITEM_LIST[0]
            """, output.Add);
        Assert.Equal(["日本語\tsecond", "日本語"], output);
        Assert.Equal(["GETITEM", "GETSLCTLST"],
            Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task SelectedListAcceptsNonpositiveOccurrence(int occurrence)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT GETSLCTLST(1, {occurrence})", output.Add);
        Assert.Equal(["日本語\tsecond"], output);
        Assert.Equal(["GETSLCTLST"], Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task SelectedColumnReachesAdapterForRouteSpecificResolution(int column)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT GETSLCTLST(1, 1, {column})", output.Add);
        Assert.Equal(["日本語\tsecond"], output);
        Assert.Equal(["GETSLCTLST"], Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("ITM_ACCCLK2")]
    [InlineData("ITM_ACCTXT2")]
    [InlineData("ITM_ACCEDIT OR 268435456")]
    public async Task AcceptsAcc2FlagsWithBackReverseAndMaximum(string kinds)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT GETITEM(1, {kinds} OR ITM_BACK OR ITM_FROMLAST, , , , 2)", output.Add);
        Assert.Equal(["2"], output);
        Assert.Equal(["GETITEM"], Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, -2)]
    [InlineData(2, int.MinValue)]
    [InlineData(16, 0)]
    [InlineData(16, -2)]
    [InlineData(16, int.MinValue)]
    [InlineData(32, 0)]
    [InlineData(32, -2)]
    [InlineData(32, int.MinValue)]
    public async Task NativeItemsAcceptZeroAndAnyNegativeOccurrence(int kind, int occurrence)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT GETITEM(1, {kind}, {occurrence})", output.Add);
        Assert.Equal(["2"], output);
        Assert.Equal(["GETITEM"], Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("GETSLCTLST()")]
    [InlineData("GETSLCTLST(1, , , 1)")]
    [InlineData("GETITEM()")]
    [InlineData("GETITEM(1)")]
    [InlineData("GETITEM(1, ITM_ACCEDIT, , , , -1)")]
    [InlineData("GETITEM(1, ITM_ACCEDIT, 0)")]
    [InlineData("GETITEM(1, ITM_ACCEDIT, -2)")]
    [InlineData("GETITEM(1, ITM_ACCEDIT, , 1)")]
    [InlineData("GETITEM(1, ITM_LIST OR ITM_BACK)")]
    [InlineData("GETITEM(1, ITM_FROMLAST)")]
    [InlineData("GETITEM(1, 268435456)")]
    [InlineData("GETITEM(1, ITM_LIST OR 268435456)")]
    [InlineData("GETITEM(1, ITM_LISTVIEW, , -2)")]
    [InlineData("GETITEM(1, ITM_LIST, , 1)")]
    [InlineData("GETITEM(1, ITM_LIST, , , 2)")]
    [InlineData("GETITEM(1, ITM_LIST, , , , 1)")]
    [InlineData("GETITEM(1, ITM_LIST, , , , , 1)")]

    [InlineData("CLKITEM(1, \"x\", CLK_BTN OR CLK_OFTHIS)")]
    [InlineData("CLKITEM(1, \"x\", CLK_LIST OR CLK_ACC OR CLK_OFTHIS)")]
    public async Task RejectsUnsupportedArgumentsBeforeAdapter(string expression)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "PRINT " + expression, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }
}
