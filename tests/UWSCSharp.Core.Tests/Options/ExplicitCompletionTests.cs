using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ExplicitCompletionTests
{
    [Theory]
    [InlineData("DIM x\nx=3\nPRINT X", "3")]
    [InlineData("PUBLIC x=1\nx=3\nPRINT x", "3")]
    [InlineData("x=3\nPRINT x\nPUBLIC x=1", "3")]
    [InlineData("DIM a[]=1,2\na[0]=3\nPRINT a[0]", "3")]
    [InlineData("HASHTBL h\nh[\"x\"]=3\nPRINT h[\"x\"]", "3")]
    [InlineData("DIM i\nFOR i=1 TO 2\nNEXT\nPRINT i", "3")]
    [InlineData("DIM x\nFOR x IN \"ab\"\nNEXT\nPRINT LENGTH(x)", "0")]
    public async Task DeclaredAndImplicitlyProvidedScopesRemainWritable(string body, string expected)
    {
        Assert.Equal([expected], await Run("OPTION EXPLICIT\n" + body));
    }

    [Theory]
    [InlineData("x=1")]
    [InlineData("PRINT x")]
    [InlineData("FOR i=1 TO 2\nNEXT")]
    [InlineData("FOR x IN \"ab\"\nNEXT")]
    [InlineData("x[0]=1")]
    public async Task UndefinedNamesFailOnExecutingStatement(string body)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("OPTION EXPLICIT\n" + body));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task UnselectedWritesAndUncalledRoutinesAreNotCheckedStatically()
    {
        Assert.Equal(["done"], await Run("""
            OPTION EXPLICIT
            IFB FALSE
                missing=1
            ENDIF
            PRINT "done"
            PROCEDURE Uncalled()
                anotherMissing=2
            FEND
            """));
    }

    [Fact]
    public async Task UnexecutedDimDoesNotDeclareRuntimeLocal()
    {
        Assert.Equal(["6"], await Run("""
            OPTION EXPLICIT
            IFB FALSE
                DIM x
            ENDIF
            TRY
                x=1
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            """));
    }

    [Fact]
    public async Task RightHandSideRunsBeforeRuntimeWriteValidation()
    {
        Assert.Equal(["rhs", "finally", "1"], await Run("""
            OPTION EXPLICIT
            PUBLIC calls=0
            TRY
                TRY
                    missing=Tick()
                FINALLY
                    PRINT "finally"
                ENDTRY
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick()
                calls=calls+1
                PRINT "rhs"
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task RightHandSideFailureKeepsItsErrorBeforeWriteValidation()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("OPTION EXPLICIT\nmissing=1/0"));
        Assert.Equal(2, error.Line);
        Assert.Contains("ゼロ", error.Message);
    }

    [Fact]
    public async Task CancellationInRightHandSideIsNotChangedToDeclarationError()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                OPTION EXPLICIT
                TRY
                    missing=Tick()
                EXCEPT
                    PRINT "caught"
                ENDTRY
                FUNCTION Tick()
                    PRINT "rhs"
                    RESULT=1
                FEND
                """, text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }, cancellation.Token));
        Assert.Equal(["rhs"], output);
    }

    [Fact]
    public async Task ParallelProgramsDoNotShareDeclarationMode()
    {
        var interpreter = new Interpreter();
        var strict = new List<string>();
        var implicitNames = new List<string>();
        await Task.WhenAll(
            interpreter.RunAsync("OPTION EXPLICIT\nDIM x=1\nPRINT x", strict.Add),
            interpreter.RunAsync("x=2\nPRINT x", implicitNames.Add));
        Assert.Equal(["1"], strict);
        Assert.Equal(["2"], implicitNames);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnyLoadedFileEnablesExplicitForWholeCompilation(bool parent, bool child)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-explicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                (child ? "OPTION EXPLICIT\n" : "") + "secondary=2");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, (parent ? "OPTION EXPLICIT\n" : "")
                + "missing=1\nCALL child\nPRINT 12");
            var host = new TestHost();
            if (parent || child)
            {
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                    new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host));
                Assert.Equal(parent ? 2 : 1, error.Line);
                Assert.Empty(host.Output);
            }
            else
            {
                await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
                Assert.Equal(["12"], host.Output);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task OptionInUnexecutedCallStillAffectsParent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-explicit-unused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"), "OPTION EXPLICIT\nDIM x=1");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, "IF FALSE THEN CALL child\nmissing=1");
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), new TestHost()));
            Assert.Equal(2, error.Line);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("OPTION EXPLICIT,")]
    [InlineData("OPTION EXPLICIT,UNKNOWN")]
    [InlineData("PRINT 1\nOPTION EXPLICIT")]
    [InlineData("OPTION EXPLICIT=FALSE")]
    public void InvalidOptionSyntaxIsRejectedBeforeExecution(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public async Task DuplicateOptionIsIdempotent()
    {
        Assert.Equal(["7"], await Run(
            "OPTION EXPLICIT\nOPTION EXPLICIT,EXPLICIT\nDIM x=7\nPRINT x"));
    }

    [Fact]
    public async Task ExternalMemberNamesAreNotScriptLocalDeclarations()
    {
        var adapter = new MemberAdapter();
        await new Interpreter(windows: adapter).RunAsync(
            "OPTION EXPLICIT\nDIM o=CREATEOLEOBJ(\"fixture\")\no.SomeProperty=7", _ => { });
        Assert.Equal(Value.Numeric(7), adapter.Assigned);
        Assert.Equal(1, adapter.Disposals);
    }

    private sealed class MemberAdapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal Value? Assigned
        {
            get; private set;
        }

        internal int Disposals
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
        {
            Assert.Equal("SomeProperty", name);
            Assigned = assigned;
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
