using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class WindowsOperationTests
{
    [Theory]
    [InlineData("GET_SLIDER")]
    [InlineData("get_slider")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task SliderQueryConstantReachesTheAdapter(string constant)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"SETSLIDER(1,{constant})", _ => { });
        (string name, Value[] arguments) =
            Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(BuiltinFunctionNames.SetSlider, name);
        Assert.Equal(Value.Numeric(-10000000), arguments[1]);
        Assert.Equal(ValueKind.Number,
            ExpressionEvaluator.Evaluate(constant).Kind);
        Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile($"CONST {constant}=3"));
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync($"{constant}=3", _ => { }));
        Assert.Equal(Value.Numeric(-10000000),
            ExpressionEvaluator.Evaluate(constant));
    }

    [Theory]
    [InlineData("MOUSEORG(1, MORG_CLIENT, MORG_FORE)")]
    [InlineData("MOUSEORG(1, MORG_DIRECT, MORG_BACK)")]
    [InlineData("MOUSEORG(1, , MORG_BACK)")]
    [InlineData("MOUSEORG(1, EMPTY, EMPTY)")]
    [InlineData("MOUSEORG(4294967297, MORG_DIRECT, MORG_BACK)")]
    [InlineData("MOUSEORG(4294967297, MORG_CLIENT)")]
    [InlineData("MOUSEORG(0)")]
    public async Task MouseOriginCaptureModesAndDirectHandlesReachTheAdapter(string script)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(script, _ => { });
        (string name, Value[] _) = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(BuiltinFunctionNames.MouseOrg, name);
    }

    [Theory]
    [InlineData("MOUSEORG()")]
    [InlineData("MOUSEORG(1, 1, 2, 0)")]
    [InlineData("MOUSEORG(1, 3)")]
    [InlineData("MOUSEORG(1, 1, -1)")]
    [InlineData("MOUSEORG(1, 1, 3)")]
    [InlineData("MOUSEORG(9007199254740992, MORG_CLIENT)")]
    [InlineData("MOUSEORG(9007199254740992, MORG_DIRECT)")]
    public async Task InvalidMouseOriginArgumentsAreRejectedBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    private sealed class Automation : IWindowsAutomation
    {
        public List<Session> Sessions { get; } = [];

        public Exception? Failure
        {
            get; init;
        }

        public IWindowsAutomationSession CreateSession()
        {
            var session = new Session(Failure);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class Session(
        Exception? failure) : IWindowsAutomationSession
    {
        public List<(string Name, Value[] Arguments)> Calls { get; } = [];

        public bool Disposed
        {
            get; private set;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                throw failure;
            }

            Calls.Add((name, arguments.ToArray()));
            return ValueTask.FromResult(name == "GETID"
                ? Value.Numeric(7) : name == "STATUS" ? Value.String(
                "fixture") : new Value(ValueKind.Empty));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("CLK_TREEVIEW OR CLK_LIST OR CLK_LEFTCLK")]
    [InlineData("CLK_LEFTCLK")]
    [InlineData("CLK_TREEVIEW OR CLK_LISTVIEW OR CLK_LEFTCLK")]
    [InlineData("CLK_LISTVIEW OR CLK_SHORT")]
    [InlineData("CLK_SHORT")]
    [InlineData("CLK_BTN OR CLK_LIST OR CLK_SHORT")]
    [InlineData("CLK_MOUSEMOVE")]
    [InlineData("CLK_BTN OR CLK_LIST OR CLK_MOUSEMOVE")]
    [InlineData("CLK_LIST OR CLK_LEFTCLK")]
    [InlineData("CLK_MENU OR CLK_LEFTCLK")]
    [InlineData("CLK_LIST OR CLK_SHORT OR CLK_LEFTCLK")]
    public async Task TypeDependentOptionsReachTheAdapter(string flags)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("CLKITEM(1, \"x\", CLK_UIA OR CLK_API)")]
    [InlineData("CLKITEM(1, \"x\", CLK_ACC OR CLK_UIA)")]
    public async Task RejectsUnsupportedAccessibilityOperationsBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Theory]
    [InlineData("CLKITEM(1, \"x\", CLK_UIA OR CLK_MENU)")]
    [InlineData("CLKITEM(1, \"x\", CLK_ACC OR CLK_MENU)")]
    [InlineData("CLKITEM(1, \"x\", CLK_UIA, FALSE)")]
    [InlineData("CLKITEM(1, \"x\", CLK_ACC, FALSE)")]
    [InlineData("CLKITEM(1, \"x\", CLK_UIA OR CLK_LIST)")]
    [InlineData("CLKITEM(1, \"x\", CLK_ACC OR CLK_LIST)")]
    [InlineData("CLKITEM(1, \"x\", CLK_BTN OR CLK_SHORT OR CLK_ACC)")]
    [InlineData("CLKITEM(1, \"x\", CLK_ACC2 OR CLK_FROMLAST, TRUE, -2)")]
    [InlineData("CLKITEM(1, \"x\", CLK_BTN OR CLK_ACC, 2)")]
    [InlineData("CLKITEM(1, \"x\", CLK_BTN OR CLK_UIA, 2)")]
    [InlineData("CLKITEM(1, \"x\", CLK_LINK OR CLK_ACC)")]
    [InlineData("CLKITEM(1, \"x\", CLK_UIA OR CLK_LINK OR CLK_BTN, FALSE)")]
    [InlineData("CLKITEM(1, \"x\", CLK_TOOLBAR OR CLK_ACC)")]
    [InlineData("CLKITEM(1, \"x\", CLK_LISTVIEW OR CLK_UIA)")]
    [InlineData("CLKITEM(1, \"x\", CLK_TREEVIEW OR CLK_ACC)")]
    [InlineData("CLKITEM(1, \"x\", CLK_TAB OR CLK_ACC)")]
    [InlineData("CLKITEM(1, \"x\", CLK_TAB OR CLK_UIA)")]
    [InlineData("CLKITEM(1, \"x\", 268468224)")]
    public async Task AccessibilityItemOperationsReachAdapter(string script)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(script, _ => { });
        (string name, Value[] _) = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(BuiltinFunctionNames.ClkItem, name);
    }

    [Theory]
    [InlineData("POSACC(1, 2)")]
    [InlineData("POSACC(-1, 2, 3)")]
    [InlineData("POSACC(1, 2, 3, ACC_API)")]
    [InlineData("POSACC(1, 2, 3, 9)")]
    [InlineData("POSACC(1, 2, 3, 16)")]
    [InlineData("POSACC(1, 2, 3, 0, 0)")]
    public async Task RejectsUnsupportedPosAccArgumentsBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Theory]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    [InlineData("POSACC(0, -100, 3)")]
    [InlineData("POSACC(0, 2, 3, ACC_NAME OR ACC_BACK)")]
    public async Task PosAccScreenCoordinatesReachTheAdapter(string script)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(script, _ => { });
        Assert.Equal(BuiltinFunctionNames.PosAcc,
            Assert.Single(Assert.Single(automation.Sessions).Calls).Name);
    }

    [Theory]
    [InlineData("PEEKCOLOR(1)")]
    [InlineData("PEEKCOLOR(1, 2, 1)")]
    [InlineData("CHKIMG(\"x.bmp\", -2)")]
    [InlineData("CHKIMG(\"x.bmp\", 5)")]
    [InlineData("CHKIMG(\"x.bmp\", , , , , , -2147483649)")]
    [InlineData("CHKIMG(\"x.bmp\", , , , , , , -1)")]
    [InlineData("SAVEIMG(\"x.bmp\", -1)")]
    [InlineData("SAVEIMG(\"x.bmp\", , , , -1)")]
    [InlineData("SAVEIMG(\"x.bmp\", , , , , , , 101)")]
    [InlineData("SAVEIMG(\"x.bmp\", , , , , , , , 3)")]
    [InlineData("SAVEIMG(\"x.bmp\", , , , , , , -2)")]
    public async Task RejectsUnsupportedImageArgumentsBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task EveryNegativeImageNumberReachesTheAdapter(int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CHKIMG(\"x.bmp\",,,,,,{number})", _ => { });
        (string name, Value[] arguments) =
            Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(BuiltinFunctionNames.ChkImg, name);
        Assert.Equal(Value.Numeric(number), arguments[6]);
    }

    [Theory]
    [InlineData(-32, 2)]
    [InlineData(-24, 1)]
    [InlineData(-16, 0)]
    [InlineData(-8, 2)]
    [InlineData(-4, 1)]
    [InlineData(-1, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(100, 0)]
    public async Task SaveImageFormatsAndCaptureModesReachTheAdapter(
        int quality, int mode)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"SAVEIMG(\"x\",,,,,,,{quality},{mode})", _ => { });
        (string name, Value[] arguments) =
            Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(BuiltinFunctionNames.SaveImg, name);
        Assert.Equal(Value.Numeric(quality), arguments[7]);
        Assert.Equal(Value.Numeric(mode), arguments[8]);
    }

    [Theory]
    [InlineData("SETSLIDER()")]
    [InlineData("SETSLIDER(1)")]
    [InlineData("SETSLIDER(1, , 1)")]
    [InlineData("SETSLIDER(EMPTY, 2, 1)")]
    [InlineData("SETSLIDER(1, 2, 2147483648)")]
    [InlineData("SETSLIDER(1, 2, 1, 0)")]
    [InlineData("SETSLIDER(1, 2147483648)")]
    public async Task RejectsUnsupportedSetSliderArgumentsBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Theory]
    [InlineData("GETSLIDER()")]
    [InlineData("GETSLIDER(EMPTY, 1)")]
    [InlineData("GETSLIDER(1, 2147483648)")]
    [InlineData("GETSLIDER(1, 1, 9)")]
    [InlineData("GETSLIDER(1, 1, -1)")]
    [InlineData("GETSLIDER(1, 1, 0, 0)")]
    public async Task RejectsUnsupportedSliderArgumentsBeforeDispatch(string script)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(script, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Fact]
    public async Task DispatchesOperationsAndUsesReturnedId()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            id = getid("fixture", "Edit", 0.25)
            PRINT STATUS(id, ST_TITLE)
            CTRLWIN(id, SHOW)
            SENDSTR(id, "日本語", , TRUE)
            MOUSEORG(id, MORG_CLIENT)
            MMV(10, 20)
            BTN(LEFT, DOWN)
            KBD(VK_CTRL, DOWN, 5)
            ACW(id, , , 640, 480)
            """, output.Add);
        Assert.Equal(["fixture"], output);
        Session session = Assert.Single(automation.Sessions);
        Assert.Equal(
            ["GETID", "STATUS", "CTRLWIN", "SENDSTR", "MOUSEORG", "MMV", "BTN", "KBD", "ACW"],
            session.Calls.Select(c => c.Name));
        Assert.Equal(Value.Numeric(7),
            session.Calls[1].Arguments[0]);
        Assert.Equal(ValueKind.Omitted,
            session.Calls[3].Arguments[2].Kind);
        Assert.Equal(Value.Numeric(17),
            session.Calls[7].Arguments[0]);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task DispatchesM3FunctionsWithOmittedArguments()
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync("""
            CLKITEM(1, "対象", CLK_BTN OR CLK_API OR CLK_BACK, , 2)
            GETSTR(1, -2, STR_STATIC)
            CHKBTN(1, "対象", , FALSE)
            """, _ => { });
        Session session = Assert.Single(automation.Sessions);
        Assert.Equal(["CLKITEM", "GETSTR", "CHKBTN"],
            session.Calls.Select(c => c.Name));
        Assert.Equal(ValueKind.Omitted, session.Calls[0].Arguments[3].Kind);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("CLK_BTN OR CLK_LEFTCLK")]
    [InlineData("CLK_BTN OR CLK_RIGHTCLK OR CLK_DBLCLK")]
    [InlineData("CLK_BTN OR CLK_MOUSEMOVE")]
    public async Task DispatchesNativeButtonPointerFlags(string flags)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task DispatchesNativeButtonNumericState(int request)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", CLK_BTN OR CLK_BACK, {request})", _ => { });
        (string Name, Value[] Arguments) call = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(Value.Numeric(request), call.Arguments[3]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-2, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(2, true)]
    public async Task DispatchesNativeMenuNumberAndShort(int number, bool partial)
    {
        var automation = new Automation();
        int flags = 4 | 512 | (partial ? 256 : 0);
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags}, TRUE, {number})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Fact]
    public async Task DispatchesNativeMenuOff()
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            "CLKITEM(1, \"item\", CLK_MENU OR CLK_BACK, FALSE)", _ => { });
        (string Name, Value[] Arguments) call = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(Value.Numeric(0), call.Arguments[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task DispatchesNativeButtonShortAndNumber(int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", CLK_BTN OR CLK_SHORT, TRUE, {number})", _ => { });
        (string Name, Value[] Arguments) call = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(Value.Numeric(number), call.Arguments[4]);
    }

    [Theory]
    [InlineData("CLK_TAB", 1)]
    [InlineData("CLK_TAB OR CLK_SHORT", 0)]
    [InlineData("CLK_TAB OR CLK_MOUSEMOVE", -1)]
    [InlineData("CLK_TAB OR CLK_LEFTCLK", 2)]
    public async Task DispatchesNativeTabOff(string flags, int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags}, FALSE, {number})", _ => { });
        (string Name, Value[] Arguments) call = Assert.Single(Assert.Single(automation.Sessions).Calls);
        Assert.Equal(Value.Numeric(0), call.Arguments[3]);
        Assert.Equal(Value.Numeric(number), call.Arguments[4]);
    }

    [Theory]
    [InlineData("CLK_TAB OR CLK_MOUSEMOVE")]
    [InlineData("CLK_TAB OR CLK_LEFTCLK")]
    [InlineData("CLK_TAB OR CLK_RIGHTCLK")]
    [InlineData("CLK_TAB OR CLK_DBLCLK")]
    [InlineData("CLK_TAB OR CLK_RIGHTCLK OR CLK_DBLCLK OR CLK_MOUSEMOVE")]
    public async Task DispatchesNativeTabPointerFlags(string flags)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-2, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(2, true)]
    [InlineData(-2, true)]
    public async Task DispatchesNativeTabNumbersAndShort(int number, bool partial)
    {
        var automation = new Automation();
        string flags = partial ? "CLK_TAB OR CLK_SHORT" : "CLK_TAB";
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags}, TRUE, {number})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task DispatchesNativeToolbarNonpositiveNumbers(int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", CLK_TOOLBAR, TRUE, {number})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task DispatchesNativeTreeNonpositiveNumbers(int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", CLK_TREEVIEW, TRUE, {number})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("", -1)]
    [InlineData("", -2)]
    [InlineData("", int.MinValue)]
    [InlineData("0", 0)]
    [InlineData("0", -1)]
    [InlineData("0", -2)]
    [InlineData("0", int.MinValue)]
    [InlineData("CLK_BTN OR CLK_LIST", 0)]
    [InlineData("CLK_BTN OR CLK_LIST", -1)]
    [InlineData("CLK_BTN OR CLK_LIST", -2)]
    [InlineData("CLK_BTN OR CLK_LIST", int.MinValue)]
    [InlineData("CLK_BTN OR CLK_TAB", 0)]
    [InlineData("CLK_BTN OR CLK_TAB", -1)]
    [InlineData("CLK_BTN OR CLK_TAB", -2)]
    [InlineData("CLK_BTN OR CLK_TAB", int.MinValue)]
    public async Task DefaultAndCombinedTypesAcceptNonpositiveNumbers(
        string flags, int number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags}, TRUE, {number})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("CLK_TOOLBAR OR CLK_MOUSEMOVE")]
    [InlineData("CLK_TOOLBAR OR CLK_LEFTCLK OR CLK_BACK")]
    [InlineData("CLK_TOOLBAR OR CLK_RIGHTCLK OR CLK_DBLCLK OR CLK_MOUSEMOVE")]
    [InlineData("CLK_TOOLBAR OR CLK_SHORT")]
    [InlineData("CLK_TOOLBAR OR CLK_SHORT OR CLK_BACK")]
    [InlineData("CLK_TREEVIEW OR CLK_SHORT")]
    [InlineData("CLK_TREEVIEW OR CLK_SHORT OR CLK_BACK")]
    [InlineData("CLK_TREEVIEW OR CLK_MOUSEMOVE")]
    [InlineData("CLK_TREEVIEW OR CLK_LEFTCLK OR CLK_BACK")]
    [InlineData("CLK_TREEVIEW OR CLK_RIGHTCLK OR CLK_DBLCLK OR CLK_API")]
    [InlineData("CLK_LISTVIEW OR CLK_MOUSEMOVE")]
    [InlineData("CLK_LISTVIEW OR CLK_LEFTCLK OR CLK_BACK")]
    [InlineData("CLK_LISTVIEW OR CLK_RIGHTCLK OR CLK_DBLCLK OR CLK_API")]
    public async Task DispatchesTreePointerFlags(string flags)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("CLK_TREEVIEW OR CLK_UIA OR CLK_LEFTCLK")]
    [InlineData("CLK_LISTVIEW OR CLK_ACC OR CLK_RIGHTCLK")]
    [InlineData("CLK_UIA OR CLK_MOUSEMOVE")]
    [InlineData("CLK_LIST OR CLK_UIA OR CLK_MOUSEMOVE")]
    [InlineData("CLK_ACC OR CLK_DBLCLK")]
    [InlineData("CLK_LIST OR CLK_UIA OR CLK_LEFTCLK")]
    [InlineData("CLK_MENU OR CLK_UIA OR CLK_RIGHTCLK")]
    [InlineData("CLK_LINK OR CLK_LEFTCLK")]
    public async Task DispatchesProviderPointerFlags(string flags)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            $"CLKITEM(1, \"item\", {flags})", _ => { });
        Assert.Single(Assert.Single(automation.Sessions).Calls);
    }

    [Theory]
    [InlineData("CLKITEM(1)")]
    [InlineData("CLKITEM(1, \"x\", CLK_LINK OR CLK_API)")]
    [InlineData("CLKITEM(1, \"x\", CLK_TREEVIEW OR CLK_OFTHIS, FALSE)")]
    [InlineData("CLKITEM(1, \"x\", , 2)")]
    [InlineData("CHKBTN(1)")]
    [InlineData("CHKBTN(1, \"x\", 2147483648)")]
    [InlineData("CHKBTN(1, \"x\", -2147483649)")]
    [InlineData("GETSTR(1, , 6)")]
    [InlineData("GETSTR(1, , , 2)")]
    [InlineData("GETSTR(1, -2147483648, STR_ACC_EDIT)")]
    [InlineData("GETSTR(1, -2147483648, STR_ACC_STATIC)")]
    [InlineData("GETSTR(1, -2147483648, STR_ACC_CELL)")]
    public async Task RejectsUnsupportedM3ArgumentsBeforeAdapter(string expression)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync("PRINT " + expression, _ => { }));
        Assert.All(automation.Sessions, session => Assert.Empty(session.Calls));
    }

    [Theory]
    [InlineData("GETID()")]
    [InlineData("GETID(\"x\", , , \"mdi\", 1)")]
    [InlineData("IDTOHND()")]
    [InlineData("IDTOHND(1, 2)")]
    [InlineData("HNDTOID()")]
    [InlineData("HNDTOID(1, 2)")]
    [InlineData("HNDTOID(9007199254740992)")]
    [InlineData("HNDTOID(-9007199254740992)")]
    [InlineData("GETID(\"x\", , -2)")]
    [InlineData("GETID(\"x\", , -0.5)")]
    [InlineData("STATUS(1, 0)")]
    [InlineData("CTRLWIN(1, -1)")]
    [InlineData("SENDSTR(1)")]
    [InlineData("SENDSTR(1, \"x\", , 3)")]
    [InlineData("SENDSTR(1, \"x\", , , 3)")]
    [InlineData("MOUSEORG(1, 3)")]
    [InlineData("MOUSEORG(1, MORG_CLIENT, 3)")]
    [InlineData("BTN(8)")]
    [InlineData("BTN(LEFT, 3)")]
    [InlineData("BTN(LEFT, , 1)")]
    [InlineData("KBD(65536)")]
    [InlineData("KBD(VK_A, 0, -1)")]
    [InlineData("MMV(1, 2, -1)")]
    [InlineData("ACW(1, , , -1)")]
    [InlineData("ACW(1, , , , -1)")]
    [InlineData("ACW(1, , , , , -1)")]
    public async Task RejectsInvalidOrUnsupportedArgumentsBeforeSideEffect(
        string source)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter(windows: automation).RunAsync(source, _ =>
        {
        }));
        Assert.Equal(1, error.Line);
        Session session = Assert.Single(automation.Sessions);
        Assert.Empty(session.Calls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task DispatchesDirectOriginConstant()
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync("""
            MOUSEORG(7, MORG_DIRECT)
            KBD(VK_A)
            MMV(10, 20)
            BTN(LEFT, CLICK, 10, 20)
            MOUSEORG(0)
            """, _ => { });
        Session session = Assert.Single(automation.Sessions);
        Assert.Equal(Value.Numeric(2), session.Calls[0].Arguments[1]);
        Assert.Equal(["MOUSEORG", "KBD", "MMV", "BTN", "MOUSEORG"],
            session.Calls.Select(call => call.Name));
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4294967295d)]
    [InlineData(9007199254740991d)]
    public async Task HandleArgumentsKeepExactNumericValue(double handle)
    {
        var automation = new Automation();
        string number = Value.Numeric(handle).Display();
        await new Interpreter(windows: automation).RunAsync(
            $"HNDTOID({number})\nIDTOHND(1)", _ => { });
        Session session = Assert.Single(automation.Sessions);
        Assert.Equal(["HNDTOID", "IDTOHND"],
            session.Calls.Select(call => call.Name));
        Assert.Equal(Value.Numeric(handle),
            session.Calls[0].Arguments[0]);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PreservesAdapterFailureAndDisposesSession()
    {
        var expected = new IOException("native failure");
        var automation = new Automation
        {
            Failure = expected
        };
        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => new Interpreter(windows: automation).RunAsync(
            "KBD(65)", _ =>
        {
        }));
        Assert.Same(expected, actual);
        Assert.True(Assert.Single(automation.Sessions).Disposed);
    }

    [Fact]
    public async Task DisposesAfterScriptFailureAndExit()
    {
        var automation = new Automation();
        var runner = new Interpreter(windows: automation);
        await Assert.ThrowsAsync<ScriptException>(() => runner.RunAsync("KBD(65, DOWN)\nPRINT 1/0", _ =>
        {
        }));
        await runner.RunAsync("KBD(65, DOWN)\nEXIT", _ =>
        {
        });
        Assert.All(automation.Sessions,
            session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task ConcurrentRunsHaveSeparateSessions()
    {
        var automation = new Automation();
        var runner = new Interpreter(windows: automation);
        await Task.WhenAll(runner.RunAsync("GETID(\"a\")", _ =>
        {
        }), runner.RunAsync("GETID(\"b\")", _ =>
        {
        }));
        Assert.Equal(2, automation.Sessions.Count);
        Assert.NotSame(automation.Sessions[0],
            automation.Sessions[1]);
        Assert.All(automation.Sessions, session =>
        {
            Assert.Single(session.Calls);
            Assert.True(session.Disposed);
        });
    }

    [Fact]
    public async Task CancellationDisposesSession()
    {
        var automation = new Automation();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter(windows: automation).RunAsync(
            "PRINT GETID(\"x\")\nKBD(65)", _ => cancellation.Cancel(),
            cancellation.Token));
        Assert.True(Assert.Single(automation.Sessions).Disposed);
    }

    [Fact]
    public async Task MissingAdapterGivesLineDiagnostic()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter().RunAsync("\nGETID(\"x\")", _ =>
        {
        }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void CompilerAndPureEvaluatorNeverPerformWindowsOperations()
    {
        _ = ScriptCompiler.Compile("GETID(\"x\")");
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate("GETID(\"x\")"));
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile("DIM GETID = 1"));
    }
}
