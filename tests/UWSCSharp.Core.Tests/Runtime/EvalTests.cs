using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class EvalTests
{
    [Fact]
    public async Task CallPrefixUsesFilesRatherThanOrdinaryRoutineNames()
    {
        List<string> output = [];
        var options = new ScriptRunOptions([])
        {
            SourcePath = Path.Combine(Path.GetTempPath(),
                Guid.NewGuid().ToString("N"), "main.uws")
        };
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("""
                EVAL("CALL OnlyRoutine()")
                PROCEDURE OnlyRoutine()
                    PRINT "unreachable"
                FEND
                """, output.Add, default, options));
        Assert.Empty(output);
    }

    [Fact]
    public async Task AssignmentReturnsStringEmptyRatherThanEmptyVariant()
    {
        var files = new CapturingFiles();
        List<string> output = [];
        await new Interpreter(files: files).RunAsync("""
            value=1
            PRINT EVAL("value:=3")=""
            FPUT(1,EVAL("value:=3"))
            PRINT value
            """, output.Add);
        Assert.Equal(["1", "3"], output);
        Assert.Equal(ValueKind.String, files.Captured.Kind);
        Assert.Equal("", files.Captured.Text);
    }

    [Fact]
    public async Task EvaluatesMemberTargetArgumentsAndValueOnceInOrder()
    {
        var adapter = new MemberAdapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PRINT EVAL("make().Value(index()) := replacement()")
            FUNCTION make()
                PRINT "target"
                RESULT = CREATEOLEOBJ("fake")
            FEND
            FUNCTION index()
                PRINT "index"
                RESULT = 1
            FEND
            FUNCTION replacement()
                PRINT "value"
                RESULT = 9
            FEND
            """, output.Add);
        Assert.Equal(["target", "index", "value", ""], output);
        Assert.Equal(Value.Numeric(9), adapter.Assigned);
        Assert.Equal(1, adapter.Calls);
    }

    private sealed class CapturingFiles : IScriptFiles, IScriptFileSession
    {
        internal Value Captured
        {
            get; private set;
        }

        public IScriptFileSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Captured = arguments[1];
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task KeepsSpecialCharacterSettingInEachCallSource()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "eval-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"), """
                OPTION SPECIALCHAR
                PRINT EVAL(CHR(34) + "<#TAB>" + CHR(34))
                """);
            List<string> output = [];
            await new Interpreter().RunAsync("""
                CALL child.uws
                PRINT EVAL(CHR(34) + "<#" + "TAB>" + CHR(34))
                """, output.Add, default,
                new ScriptRunOptions([])
                {
                    SourcePath = Path.Combine(folder, "main.uws")
                });
            Assert.Equal(["<#TAB>", "\t"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RunsHelpExampleAndHonorsStringOptions()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            a = 2
            b = 4
            c = 0
            EVAL("c := 3")
            PRINT EVAL("int(a * b + c - subr() / 2)")
            FUNCTION subr()
                RESULT = 9.9
            FEND
            """, output.Add);
        Assert.Equal(["6"], output);
        output.Clear();
        await new Interpreter().RunAsync("""
            OPTION SPECIALCHAR, SAMESTR
            quote = CHR(34)
            PRINT EVAL(quote + "a" + quote + "=" + quote + "A" + quote)
            PRINT EVAL(quote + "<#TAB>" + quote)
            """, output.Add);
        Assert.Equal(["0", "<#TAB>"], output);
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync("""
                PRINT EVAL("slow()")
                FUNCTION slow()
                    SLEEP(60)
                    RESULT = 1
                FEND
                """, _ => { }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ReadsCallerScopeComparesAndAssignsValues()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC shared = 10
            DIM items[] = 2, 3
            PRINT EVAL("1 + 2 * 3")
            PRINT EVAL("shared = 10")
            PRINT shared
            PRINT EVAL("shared := 20")
            PRINT shared
            PRINT EVAL("items[1] := 8")
            PRINT items[1]
            PRINT EVAL("1 + (shared := 5)")
            PRINT local(4)
            FUNCTION local(value)
                DIM hidden = 3
                RESULT = EVAL("value + hidden + shared")
            FEND
            """, output.Add);
        Assert.Equal(["7", "1", "10", "", "20", "", "8", "1", "12"], output);
    }

    [Fact]
    public async Task CallsFunctionsAndRestoresDepthAfterFailure()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            expression = "EVAL(expression)"
            TRY
                PRINT EVAL(expression)
            EXCEPT
                PRINT "limited"
            ENDTRY
            PRINT EVAL("COPY(<#DBL>abc<#DBL>, 2, 2)")
            PRINT EVAL("twice(6)")
            FUNCTION twice(value)
                RESULT = value * 2
            FEND
            """, output.Add);
        Assert.Equal(["limited", "bc", "12"], output);
    }

    [Theory]
    [InlineData("EVAL()")]
    [InlineData("EVAL(,)")]
    [InlineData("EVAL(1)")]
    [InlineData("EVAL(NULL)")]
    [InlineData("EVAL(\"1+\")")]
    [InlineData("EVAL(\"1:=2\")")]
    [InlineData("EVAL(\"CUR_ARROW:=3\")")]
    [InlineData("EVAL(\"locked:=3\")")]
    [InlineData("EVAL(\"newname:=3\")")]
    public async Task RejectsInvalidInputAndPreservesConstants(string expression)
    {
        List<string> output = [];
        await new Interpreter().RunAsync($"""
            OPTION EXPLICIT
            CONST locked = 5
            TRY
                PRINT {expression}
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT locked
            """, output.Add);
        Assert.Equal(["caught", "5"], output);
    }

    [Fact]
    public async Task RejectsExcessArgumentsBeforeSideEffects()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            TRY
                EVAL(side(), side())
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION side()
                PRINT "side"
                RESULT = "1"
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("EVAL(\"1\")"));
    }

    [Fact]
    public async Task RecursiveLimitKeepsWorkerContinuationAndRestoresDepth()
    {
        List<string> output = [];
        List<int> threads = [];
        await using var worker = new WorkerThreadContext(async token =>
        {
            await new Interpreter().RunAsync("""
                expression="EVAL(expression)"
                TRY
                    PRINT EVAL(expression)
                EXCEPT
                    PRINT "limited"
                ENDTRY
                PRINT EVAL("6*7")
                """, text =>
                {
                    output.Add(text);
                    threads.Add(Environment.CurrentManagedThreadId);
                }, token);
        });
        await worker.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["limited", "42"], output);
        Assert.All(threads, thread => Assert.Equal(worker.ManagedThreadId, thread));
    }

    private sealed class MemberAdapter : IWindowsAutomation,
        IWindowsAutomationSession
    {
        internal Value? Assigned;
        internal int Calls;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target,
            string name, IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken token)
        {
            Assert.Equal("Value", name);
            Assert.Equal([Value.Numeric(1)], arguments);
            Assigned = assigned;
            Calls++;
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
