using UWSCSharp.Testing;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests : IDisposable
{
    private readonly TemporaryTestDirectory temporary =
        new("uwsc-call-", "N");

    private string folder => temporary.Path;

    private string Write(string name, string source)
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, true)]
    [InlineData(2047, true)]
    [InlineData(2048, false)]
    public void CallArgumentParsingUsesSharedTokenLimit(int count, bool accepted)
    {
        Write("child.uws", "PRINT 1");
        string arguments = string.Join(",", Enumerable.Repeat("1", count));
        string main = Write("main.uws", $"CALL child({arguments})");
        if (accepted)
        {
            Assert.NotNull(ScriptCompiler.CompileFile(main));
        }
        else
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.CompileFile(main));
            Assert.Equal(1, error.Line);
            Assert.Contains("式が長すぎます。", error.Message);
        }
    }

    [Theory]
    [InlineData(126, true)]
    [InlineData(127, false)]
    public void CallArgumentParsingKeepsExistingNestingLimit(int count, bool accepted)
    {
        Write("child.uws", "PRINT 1");
        string argument = new string('(', count) + "1" + new string(')', count);
        string main = Write("main.uws", $"CALL child({argument})");
        if (accepted)
        {
            Assert.NotNull(ScriptCompiler.CompileFile(main));
        }
        else
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.CompileFile(main));
            Assert.Equal(1, error.Line);
            Assert.Contains("式の入れ子が深すぎます。", error.Message);
        }
    }

    [Fact]
    public void CallRejectsExpressionAfterArgumentsWithScriptLine()
    {
        Write("child.uws", "PRINT 1");
        string main = Write("main.uws", "\nCALL child(1)+2*(3)");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(2, error.Line);
        Assert.Contains("式の末尾が不正です: +", error.Message);
    }

    [Fact]
    public async Task ImportsDefinitionsAndUsesSeparateLocalsAndArguments()
    {
        Write("library.uws", """
            PUBLIC shared=0
            DIM local=0
            local=local+1
            shared=shared+VAL(PARAM_STR[1])
            PRINT PARAM_STR[0]
            PRINT local
            FUNCTION Helper()
                RESULT=shared
            FEND
            """);
        string main = Write("main.uws", """
            CALL library("first",12)
            CALL library.uws("second",3)
            PRINT Helper()
            PRINT PARAM_STR[0]
            """);
        var output = new List<string>();
        await new Interpreter().RunAsync(Source.ReadFile(main), output.Add, default,
            new ScriptRunOptions(["root"]) { SourcePath = main });
        Assert.Equal(["first", "1", "second", "1", "15", "root"], output);
    }

    [Fact]
    public async Task NestedRelativeQuotedPathsRestoreParentArguments()
    {
        Write("sub/child file.uws", """
            PRINT PARAM_STR[0]
            CALL nested("inner")
            PRINT PARAM_STR[0]
            """);
        Write("sub/nested.uws", "PRINT PARAM_STR[0]");
        string main = Write("main.uws", "CALL \"sub/child file.uws\"(\"outer\")\nPRINT PARAM_STR[0]");
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), default, new ScriptRunOptions(["root"]));
        Assert.Equal(["outer", "inner", "outer", "root"], output);
    }

    [Fact]
    public async Task CompileSnapshotCanRunAfterFilesChangeOrDisappear()
    {
        string child = Write("child.uws", "PRINT \"old\"");
        string main = Write("main.uws", "CALL child");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        File.Delete(child);
        var output = new List<string>();
        var interpreter = new Interpreter();
        await interpreter.RunAsync(program, new CallbackScriptHost(output.Add));
        await interpreter.RunAsync(program, new CallbackScriptHost(output.Add));
        Assert.Equal(["old", "old"], output);
    }

    [Fact]
    public async Task RuntimeFailureRestoresArgumentsAndRetainsSourcePath()
    {
        string child = Write("child.uws", "PRINT 1/0");
        string main = Write("main.uws", """
            TRY
                CALL child("child")
            FINALLY
                PRINT PARAM_STR[0]
            ENDTRY
            """);
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(Source.ReadFile(main), output.Add, default,
                new ScriptRunOptions(["root"]) { SourcePath = main }));
        Assert.Equal(["root"], output);
        Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task CircularFilesCompileOnceButRuntimeRecursionIsBounded()
    {
        Write("child.uws", "CALL main");
        string main = Write("main.uws", "CALL child");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(program, new CallbackScriptHost(_ => { })));
        Assert.Contains("階層", error.Message);
    }


    [Fact]
    public void DuplicateImportedDefinitionsFailBeforeExecution()
    {
        Write("a.uws", "FUNCTION F()\nRESULT=1\nFEND");
        Write("b.uws", "FUNCTION F()\nRESULT=2\nFEND");
        string main = Write("main.uws", "CALL a\nCALL b");
        Assert.Throws<ScriptException>(() => ScriptCompiler.CompileFile(main));
    }

    [Fact]
    public async Task ExitOnlyReturnsFromCalledMain()
    {
        Write("child.uws", "EXIT\nPRINT \"unreachable\"");
        string main = Write("main.uws", "CALL child\nPRINT \"parent\"");
        var output = new List<string>();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), new CallbackScriptHost(output.Add));
        Assert.Equal(["parent"], output);
    }

    private sealed class CancelHost(CancellationTokenSource cancellation) : IScriptHost
    {
        internal List<string> Output { get; } = [];

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }
    }

    [Fact]
    public async Task CancellationRunsChildFinallyThenRestoresArgumentsForParentFinally()
    {
        Write("child.uws", "TRY\nSLEEP(1)\nFINALLY\nPRINT PARAM_STR[0]\nENDTRY");
        string main = Write("main.uws", """
            OPTION OPTFINALLY
            TRY
                CALL child("child")
            FINALLY
                PRINT PARAM_STR[0]
            ENDTRY
            """);
        using var cancellation = new CancellationTokenSource();
        var host = new CancelHost(cancellation);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host, cancellation.Token,
                new ScriptRunOptions(["root"])));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["child", "root"], host.Output);
    }

    [Fact]
    public void MissingChildIsReportedDuringCompilationAndPreCanceledReadIsSkipped()
    {
        string main = Write("main.uws", "CALL missing");
        IOException error = Assert.ThrowsAny<IOException>(() => ScriptCompiler.CompileFile(main));
        Assert.Equal(Path.Combine(folder, "missing.uws"), error.Data["UWSCSharp.SourcePath"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScriptCompiler.CompileFile(Path.Combine(folder, "absent.uws"), cancellation.Token));
    }

    [Fact]
    public void BreakInChildCannotTargetCallingScriptLoop()
    {
        string child = Write("child.uws", "BREAK");
        string main = Write("main.uws", "FOR i=1 TO 2\nCALL child\nNEXT");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(1, error.Line);
        Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task ChildLocalBreakReturnsToCallingScript()
    {
        Write("child.uws", "FOR j=1 TO 2\nBREAK\nNEXT\nPRINT j");
        string main = Write("main.uws",
            "FOR i=1 TO 2\nCALL child\nNEXT\nPRINT i");
        var host = new ControlFlowTests.TestHost();
        await new Interpreter().RunAsync(
            ScriptCompiler.CompileFile(main), host);
        Assert.Equal(["1", "1", "3"], host.Output);
    }

    [Theory]
    [InlineData("EXIT", 0)]
    [InlineData("EXITEXIT 7", 7)]
    public async Task ExitUnwindsOnlyRequiredCallFrames(string instruction, int code)
    {
        Write("leaf.uws", "TRY\n" + instruction
            + "\nFINALLY\nPRINT \"leaf\"\nENDTRY\nPRINT 1/0");
        Write("child.uws", "TRY\nCALL leaf\nPRINT \"child-after\""
            + "\nFINALLY\nPRINT \"child\"\nENDTRY");
        string main = Write("main.uws", "TRY\nCALL child\nPRINT \"main-after\""
            + "\nFINALLY\nPRINT \"main\"\nENDTRY");
        var host = new ControlFlowTests.TestHost();
        Assert.Equal(code, await new Interpreter().RunWithExitCodeAsync(
            ScriptCompiler.CompileFile(main), host, default, ScriptRunOptions.Empty));
        Assert.Equal(instruction == "EXIT"
            ? ["leaf", "child-after", "child", "main-after", "main"]
            : new[] { "leaf", "child", "main" }, host.Output);
    }

    public void Dispose() => temporary.Dispose();
}
