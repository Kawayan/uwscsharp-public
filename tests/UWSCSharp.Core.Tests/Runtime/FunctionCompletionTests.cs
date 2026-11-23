using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FunctionCompletionTests
{
    [Theory]
    [InlineData("7", "7")]
    [InlineData("HASH_REMOVEALL", "-109")]
    public async Task ResultAssignmentReplacesHashWithNewReturnValue(
        string expression, string expected)
    {
        Assert.Equal([expected], await Run("PRINT F()\nFUNCTION F()\nHASHTBL h\n"
            + "RESULT=h\nRESULT=" + expression + "\nFEND"));
    }

    [Fact]
    public async Task ProcedureResultAlsoReplacesHashWithoutChangingItsEmptyReturn()
    {
        Assert.Equal(["7", "0"], await Run("PRINT LENGTH(P())\nPROCEDURE P()\n"
            + "HASHTBL h\nRESULT=h\nRESULT=7\nPRINT RESULT\nFEND"));
    }

    [Theory]
    [InlineData("h")]
    [InlineData("RESULT")]
    public async Task MainHashVariablesRetainClearCommandAndTypeRestriction(string name)
    {
        Assert.Equal(["0"], await Run("HASHTBL " + name + "\n" + name
            + "[\"x\"]=1\n" + name + "=HASH_REMOVEALL\nPRINT LENGTH(" + name + ")"));
        await Assert.ThrowsAsync<ScriptException>(() => Run(
            "HASHTBL " + name + "\n" + name + "=7"));
    }

    [Fact]
    public async Task OrdinaryHashInsideFunctionRetainsClearAndTypeRules()
    {
        Assert.Equal(["0", "rejected"], await Run("""
            PRINT Clear()
            TRY
                ReplaceHashWithNumber()
            EXCEPT
                PRINT "rejected"
            ENDTRY
            FUNCTION Clear()
                HASHTBL h
                h["x"]=1
                h=HASH_REMOVEALL
                RESULT=LENGTH(h)
            FEND
            FUNCTION ReplaceHashWithNumber()
                HASHTBL h
                h=7
            FEND
            """));
    }

    [Fact]
    public async Task CallFromFunctionDoesNotMakeChildResultAReturnSlot()
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-result-call-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "HASHTBL RESULT\nRESULT[\"x\"]=1\nRESULT=HASH_REMOVEALL\nPRINT LENGTH(RESULT)");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, "PRINT F()\nFUNCTION F()\nCALL child\nRESULT=7\nFEND");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            Assert.Equal(["0", "7"], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-7", "-7")]
    [InlineData("0.5", "0.5")]
    [InlineData("TRUE", "1")]
    [InlineData("\"text\"", "text")]
    [InlineData("\"\"", "")]
    [InlineData("EMPTY", "")]
    public async Task ResultReturnsItsAssignedScalar(string expression, string expected)
    {
        Assert.Equal([expected], await Run("PRINT F()\nFUNCTION F()\nRESULT="
            + expression + "\nFEND"));
    }

    [Fact]
    public async Task UnassignedResultIsEmptyForEveryCall()
    {
        Assert.Equal(["7", "0"], await Run("""
            PRINT F(TRUE)
            PRINT LENGTH(F(FALSE))
            FUNCTION F(assign)
                IF assign THEN RESULT=7
            FEND
            """));
    }

    [Fact]
    public async Task PublicAndNestedResultsDoNotChangeCurrentResult()
    {
        Assert.Equal(["8", "99"], await Run("""
            PUBLIC RESULT=99
            PRINT Outer()
            PRINT RESULT
            FUNCTION Outer()
                RESULT=7
                Inner()
                RESULT=RESULT+1
            FEND
            FUNCTION Inner()
                RESULT=50
            FEND
            """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinallySetsResultBeforeNormalAndExitReturn(bool early)
    {
        Assert.Equal(["6", "8"], await Run("x=1\nPRINT F(x)\nPRINT x\nFUNCTION F(VAR a)\n"
            + "TRY\na=3\nRESULT=4\n" + (early ? "EXIT\n" : "")
            + "FINALLY\na=8\nRESULT=6\nENDTRY\nFEND"));
    }

    [Fact]
    public async Task FinallyFailurePreventsResultAndReferenceWriteback()
    {
        Assert.Equal(["0", "1"], await Run("""
            x=1
            value=0
            TRY
                value=F(x)
            EXCEPT
                PRINT value
                PRINT x
            ENDTRY
            FUNCTION F(VAR a)
                TRY
                    a=9
                    RESULT=7
                FINALLY
                    PRINT INT(NULL)
                ENDTRY
            FEND
            """));
    }

    [Fact]
    public async Task ArrayAndHashReturnsAreIndependentSnapshots()
    {
        Assert.Equal(["1,2", "1"], await Run("""
            a=ArrayValue()
            PRINT JOIN(a,",")
            h=HashValue()
            PRINT h["x"]
            FUNCTION ArrayValue()
                DIM values[]=1,2
                RESULT=values
                values[0]=9
            FEND
            FUNCTION HashValue()
                HASHTBL values
                values["x"]=1
                RESULT=values
                values["x"]=9
            FEND
            """));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task FunctionRecursionUsesCommonDepthLimit(int depth, bool accepted)
    {
        string source = "PRINT F(" + depth + ")\nFUNCTION F(n)\n"
            + "IF n=1 THEN RESULT=1 ELSE RESULT=1+F(n-1)\nFEND";
        if (accepted)
        {
            Assert.Equal(["128"], await Run(source));
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
            Assert.Equal(3, error.Line);
        }
    }

    [Fact]
    public async Task ReturnedValuesPreserveKindAndReferenceUntilSessionDisposal()
    {
        Value[] values = [Value.Empty, new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(UwscArray.FromValues([Value.Numeric(7)])),
            Value.FromHashTable(UwscHashTable.Create()), Value.String("text"), Value.Numeric(3)];
        foreach (Value value in values)
        {
            var adapter = new ResultAdapter(value);
            await new Interpreter(windows: adapter).RunAsync("""
                o=CREATEOLEOBJ("fixture")
                o.Accept(F(o.Value))
                FUNCTION F(x)
                    RESULT=x
                FEND
                """, _ => { });
            Value returned = Assert.Single(adapter.Accepted);
            Assert.Equal(value, returned);
            Assert.Equal(1, adapter.Disposals);
        }
    }

    [Fact]
    public async Task CancellationDuringFinallyDoesNotPublishFunctionResult()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                PRINT F()
                FUNCTION F()
                    TRY
                        RESULT=7
                    FINALLY
                        PRINT "cleanup"
                    ENDTRY
                FEND
                """, text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }, cancellation.Token));
        Assert.Equal(["cleanup"], output);
    }

    private sealed class ResultAdapter(Value value) : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<Value> Accepted
        {
            get;
        } = [];

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
            if (name == "Value")
            {
                return ValueTask.FromResult(value);
            }

            Assert.Equal("Accept", name);
            Assert.Equal(0, Disposals);
            Accepted.Add(Assert.Single(arguments));
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
