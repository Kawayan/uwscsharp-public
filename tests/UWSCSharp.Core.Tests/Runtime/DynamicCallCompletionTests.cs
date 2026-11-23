using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DynamicCallCompletionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        $"uwsc-dynamic-call-{Guid.NewGuid():N}");

    public DynamicCallCompletionTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task NativeDllDeclarationsAreVisibleDuringAndAfterImport()
    {
        Write("child.uws", """
            PUBLIC observed=""
            IF FALSE THEN DEF_DLL ChildExport():missing.dll
            IF FALSE THEN DEF_DLL ROOTEXPORT():missing.dll
            MODULE ImportSetup
                PROCEDURE ImportSetup()
                    observed=Observe()
                FEND
            ENDMODULE
            FUNCTION Observe()
                TRY
                    RESULT=ChildExport()
                EXCEPT
                    RESULT=TRY_ERRMSG
                ENDTRY
            FEND
            """);
        string main = Write("main.uws", """
            DEF_DLL RootExport():missing.dll
            EVAL("CALL child")
            PRINT observed
            PRINT Observe()
            TRY
                PRINT RootExport()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            """);
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));

        Assert.Collection(output,
            initializing => Assert.EndsWith(": ChildExport", initializing),
            published => Assert.EndsWith(": ChildExport", published),
            existing => Assert.EndsWith(": RootExport", existing));
        Assert.All(output, message => Assert.Contains("DEF_DLL", message));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeDllUpdatesFollowTheOutcomeOfImportInitialization(
        bool succeeds)
    {
        string result = succeeds ? "1" : "MissingRoutine()";
        Write("child.uws", $$"""
            MODULE ImportSetup
                PROCEDURE ImportSetup()
                    Initialize()
                FEND
            ENDMODULE
            PROCEDURE Initialize()
                DEF_DLL ROOTEXPORT():missing.dll
                DEF_DLL ChildExport():missing.dll
                DIM result={{result}}
            FEND
            """);
        string main = Write("main.uws", """
            DEF_DLL RootExport():missing.dll
            TRY
                EVAL("CALL child")
            EXCEPT
                PRINT "import failed"
            ENDTRY
            TRY
                PRINT RootExport()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            TRY
                PRINT ChildExport()
            EXCEPT
                PRINT TRY_ERRMSG
            ENDTRY
            """);
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));

        if (succeeds)
        {
            Assert.Collection(output,
                existing => Assert.EndsWith(": ROOTEXPORT", existing),
                added => Assert.Contains("DEF_DLL", added));
        }
        else
        {
            Assert.Collection(output,
                failure => Assert.Equal("import failed", failure),
                existing => Assert.EndsWith(": RootExport", existing),
                hidden => Assert.Contains("未定義の関数: ChildExport", hidden));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsFileArgumentsDistinctFromRoutineCalls(bool quoted)
    {
        Write("child.uws", "PRINT PARAM_STR[0]");
        string target = quoted ? "<#DBL>child.uws<#DBL>" : "child.uws";
        string main = Write("main.uws",
            $"EVAL(\"CALL {target}(<#DBL>argument<#DBL>)\")");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["argument"], output);
    }

    [Fact]
    public async Task EvaluatesQuotedCallTargetExpressionOnce()
    {
        Write("child.uws", "PRINT \"child\"");
        string main = Write("main.uws",
            "EVAL(\"CALL <#DBL>chi<#DBL>+<#DBL>ld.uws<#DBL>\")");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["child"], output);
    }

    [Fact]
    public async Task RegistersDynamicGlobalsRoutinesAndModulesAndInitializesOnlyOnce()
    {
        Write("child.uws", """
            PUBLIC shared = 7
            PRINT "child"
            FUNCTION added(value)
                RESULT = value + shared
            FEND
            MODULE Box
                PUBLIC value = 2
                PROCEDURE Box()
                    value = value + 1
                FEND
            ENDMODULE
            """);
        string main = Write("main.uws", """
            DIM ignored = EVAL("CALL child")
            PRINT shared
            PRINT added(3)
            PRINT Box.value
            ignored = EVAL("CALL child")
            PRINT Box.value
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["child", "7", "10", "3", "child", "3"], output);
    }

    [Fact]
    public async Task ResolvesNestedEvalCallsFromTheImportedRoutineFile()
    {
        Write("sub/child.uws", """
            FUNCTION loadMore()
                DIM ignored = EVAL("CALL grand")
            FEND
            """);
        Write("sub/grand.uws", "PUBLIC shared = 9\nPRINT \"grand\"");
        string main = Write("main.uws", """
            DIM ignored = EVAL("CALL sub/child")
            loadMore()
            PRINT shared
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["grand", "9"], output);
    }

    [Fact]
    public async Task SharesStaticCallDefinitionsWithoutReinitializingThem()
    {
        Write("child.uws", """
            PRINT "child"
            MODULE Box
                PROCEDURE Box()
                    initializations = initializations + 1
                FEND
            ENDMODULE
            """);
        string main = Write("main.uws", """
            PUBLIC initializations = 0
            IF FALSE THEN CALL child
            DIM ignored = EVAL("CALL child")
            ignored = EVAL("CALL child")
            PRINT initializations
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["child", "child", "1"], output);
    }

    [Fact]
    public async Task AllowsEvalCallReentryFromTheLoadedMainAfterInitialization()
    {
        Write("child.uws", "DIM ignored = EVAL(\"CALL grand\")");
        Write("grand.uws", "PUBLIC shared = 11");
        string main = Write("main.uws", """
            DIM ignored = EVAL("CALL child")
            PRINT shared
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["11"], output);
    }

    [Fact]
    public async Task RejectsDuplicatesBeforePublishingIncomingGlobals()
    {
        Write("child.uws", """
            PUBLIC incoming = 1
            FUNCTION existing()
                RESULT = 2
            FEND
            """);
        string main = Write("main.uws", """
            TRY
                DIM ignored = EVAL("CALL child")
            EXCEPT
                PRINT "duplicate"
            ENDTRY
            PRINT existing()
            TRY
                PRINT incoming
            EXCEPT
                PRINT "absent"
            ENDTRY
            FUNCTION existing()
                RESULT = 7
            FEND
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["duplicate", "7", "absent"], output);
    }

    [Fact]
    public async Task RetainsCompletedPublicUpdatesButDoesNotRepeatFailedInitialization()
    {
        Write("child.uws", """
            PUBLIC attempts = attempts + 1
            PUBLIC invalid = missing
            FUNCTION incoming()
                RESULT = 1
            FEND
            """);
        string main = Write("main.uws", """
            PUBLIC attempts = 0
            TRY
                DIM ignored = EVAL("CALL child")
            EXCEPT
                PRINT attempts
            ENDTRY
            TRY
                DIM ignored = EVAL("CALL child")
            EXCEPT
                PRINT attempts
            ENDTRY
            TRY
                PRINT incoming()
            EXCEPT
                PRINT "unpublished"
            ENDTRY
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["1", "1", "unpublished"], output);
    }

    [Fact]
    public async Task RejectsInitializerCallReentryWithoutDeadlocking()
    {
        Write("child.uws", """
            PUBLIC attempts = attempts + 1
            PUBLIC invalid = EVAL("CALL nested")
            """);
        Write("nested.uws", "PRINT \"unexpected\"");
        string main = Write("main.uws", """
            PUBLIC attempts = 0
            TRY
                DIM ignored = EVAL("CALL child")
            EXCEPT
                PRINT attempts
            ENDTRY
            """);
        var output = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), timeout.Token);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneInitializationAndExecuteBothMains()
    {
        Write("child.uws", "PUBLIC attempts = attempts + 1\nSLEEP(0.01)");
        string main = Write("main.uws", """
            PUBLIC attempts = 0
            PUBLIC firstDone = 0
            PUBLIC secondDone = 0
            THREAD first()
            THREAD second()
            WHILE firstDone = 0 OR secondDone = 0
                SLEEP(0.001)
            WEND
            PRINT attempts
            PROCEDURE first()
                DIM ignored = EVAL("CALL child")
                firstDone = 1
            FEND
            PROCEDURE second()
                DIM ignored = EVAL("CALL child")
                secondDone = 1
            FEND
            """);
        ConcurrentQueue<string> output = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Enqueue), timeout.Token);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task ReusingAProgramDoesNotRetainDynamicDefinitionsOrChangeItsGraph()
    {
        Write("child.uws", "PUBLIC attempts = attempts + 1");
        string main = Write("main.uws", """
            PUBLIC attempts = 0
            DIM ignored = EVAL("CALL child")
            PRINT attempts
            """);
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        var first = new List<string>();
        var second = new List<string>();
        var interpreter = new Interpreter();
        await interpreter.RunAsync(program, new CallbackScriptHost(first.Add));
        await interpreter.RunAsync(program, new CallbackScriptHost(second.Add));
        Assert.Equal(["1"], first);
        Assert.Equal(first, second);
        Assert.Single(program.ExternalScripts);
        Assert.Empty(program.ExternalScripts[0].Dependencies);
    }

    [Fact]
    public async Task PreservesTargetSyntaxErrorSourceAndLine()
    {
        string child = Write("child.uws", "PRINT 1\nDIM value=(");
        string main = Write("main.uws", "DIM ignored=EVAL(\"CALL child\")");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task ReportsRuntimeFileReadFailureAtTheCallSiteWithTargetPath()
    {
        string main = Write("main.uws", "PRINT 1\nDIM ignored=EVAL(\"CALL missing\")");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(Path.Combine(directory, "missing.uws"),
            error.Data["UWSCSharp.SourcePath"]);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task ObservesCancellationInsideTheDynamicCallMain()
    {
        Write("child.uws", "PRINT \"cancel\"\nPRINT \"unreachable\"");
        string main = Write("main.uws", "DIM ignored=EVAL(\"CALL child\")");
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }), cancellation.Token));
        Assert.Equal(["cancel"], output);
    }

    [Fact]
    public async Task RejectsSpecialCharacterPolicyConflictsAtTheEvalCaller()
    {
        Write("target.uws", "PRINT \"unexpected\"");
        string loader = Write("loader.uws", """
            OPTION SPECIALCHAR
            DIM ignored = EVAL("CALL target")
            """);
        string main = Write("main.uws", """
            IF FALSE THEN CALL target
            DIM ignored = EVAL("CALL loader")
            """);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(loader, error.Data["UWSCSharp.SourcePath"]);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task AggregatesDynamicOptionsForSubsequentStatements()
    {
        Write("child.uws", "OPTION SAMESTR, EXPLICIT\nPUBLIC incoming = 1");
        string main = Write("main.uws", """
            DIM ignored = EVAL("CALL child")
            PRINT "a" = "A"
            TRY
                undeclared = 1
            EXCEPT
                PRINT incoming
            ENDTRY
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["0", "1"], output);
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public async Task CountsDynamicAndStaticFilesAgainstTheSameLimit(int count, bool succeeds)
    {
        for (int index = 0; index < count; index++)
        {
            Write($"child{index}.uws", index + 1 < count
                ? $"CALL child{index + 1}" : "PRINT 1");
        }

        string main = Write("main.uws", "DIM ignored=EVAL(\"CALL child0\")");
        var output = new List<string>();
        Task run = new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        if (succeeds)
        {
            await run;
            Assert.Equal(["1"], output);
        }
        else
        {
            await Assert.ThrowsAsync<ScriptException>(() => run);
            Assert.Empty(output);
        }
    }

    [Fact]
    public async Task BoundsRecursiveDynamicCalls()
    {
        Write("child.uws", "DIM ignored=EVAL(\"CALL child\")");
        string main = Write("main.uws", "DIM ignored=EVAL(\"CALL child\")");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync(
            ScriptCompiler.CompileFile(main), new CallbackScriptHost(_ => { }), timeout.Token));
    }
}
