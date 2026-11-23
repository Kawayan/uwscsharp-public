using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class DefinitionCompletionTests
{
    [Theory]
    [InlineData("PROCEDURE", "F()", "2")]
    [InlineData("FUNCTION", "F()", "2")]
    [InlineData("PROCEDURE", "F(,)", "2")]
    [InlineData("FUNCTION", "F(,)", "2")]
    public async Task OmittedVarDefaultHasNoCallerWriteback(
        string kind, string call, string expected)
    {
        Assert.Equal([expected, "7", "3"], await Run("CONST seed=3\n"
            + call + "\nPRINT seed\n" + kind + " F(VAR x=2,y=seed)\n"
            + "PRINT x\nx=9\ny=7\nPRINT y\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task ExplicitVarStillWritesBackAndRejectsConstant(string kind)
    {
        Assert.Equal(["9", "error", "3"], await Run("CONST seed=3\na=1\nF(a)\n"
            + "PRINT a\nTRY\nF(seed)\nEXCEPT\nPRINT \"error\"\nENDTRY\n"
            + "PRINT seed\n" + kind + " F(VAR x=2)\nx=9\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task DefaultsReadEarlierFormalsAndCurrentGlobals(string kind)
    {
        Assert.Equal(["3", "4", "7", "8", "9", "10"], await Run(
            "PUBLIC shared=3\nF()\nshared=7\nF()\nF(9)\n"
            + kind + " F(a=shared,b=a+1)\nPRINT a\nPRINT b\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE", "")]
    [InlineData("FUNCTION", "")]
    [InlineData("PROCEDURE", "VAR ")]
    [InlineData("FUNCTION", "VAR ")]
    public async Task DefaultArrayFromEarlierFormalIsAnIndependentSnapshot(
        string kind, string modifier)
    {
        Assert.Equal(["1,2", "1,2", "1,2", "1,2"], await Run(
            "DIM seed[]=1,2\nF(seed)\nPRINT JOIN(seed,\",\")\n"
            + "F(seed)\nPRINT JOIN(seed,\",\")\n" + kind
            + " F(a[]," + modifier + "x[]=a)\nPRINT JOIN(x,\",\")\nx[0]=9\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task ArrayDefaultUsesPublicSnapshotWithoutWritingItBack(string kind)
    {
        Assert.Equal(["9,2", "1,2"], await Run("PUBLIC seed=SPLIT(\"1 2\")\n"
            + "F()\nPRINT JOIN(seed,\",\")\n" + kind
            + " F(VAR x[]=seed)\nx[0]=9\nPRINT JOIN(x,\",\")\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task ExplicitArrayVarKeepsWritebackWithAnUnusedDefault(string kind)
    {
        Assert.Equal(["9,2"], await Run("DIM a[]=1,2\nF(a)\nPRINT JOIN(a,\",\")\n"
            + kind + " F(VAR x[]=missing)\nx[0]=9\nFEND"));
    }

    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("3")]
    public async Task ArrayDefaultsAreRankCheckedOnlyWhenUsed(string fallback)
    {
        Assert.Equal(["body", "error"], await Run("DIM a[]=1,2\nF(a)\nTRY\nF()\n"
            + "EXCEPT\nPRINT \"error\"\nENDTRY\nPROCEDURE F(x[]=" + fallback
            + ")\nPRINT \"body\"\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task IndexedDefaultKeepsRemainingArrayAxes(string kind)
    {
        Assert.Equal(["3,4", "4"], await Run("DIM seed[1][1]=1,2,3,4\nF(seed)\n"
            + kind + " F(a[][],row[]=a[1],last=row[1])\n"
            + "PRINT JOIN(row,\",\")\nPRINT last\nFEND"));
    }

    [Fact]
    public async Task CallerLocalsDoNotLeakIntoOmittedDefaults()
    {
        Assert.Equal(["7", "error"], await Run("local=99\nF(7)\nTRY\nF()\nEXCEPT\n"
            + "PRINT \"error\"\nENDTRY\nPROCEDURE F(x=local)\nPRINT x\nFEND"));
    }

    [Fact]
    public async Task FailedDefaultDoesNotRunBodyOrCopyBackEarlierVar()
    {
        Assert.Equal(["error", "1"], await Run("a=1\nTRY\nF(a)\nEXCEPT\nPRINT \"error\"\n"
            + "ENDTRY\nPRINT a\nPROCEDURE F(VAR x,y=missing)\nx=9\nPRINT \"body\"\nFEND"));
    }

    [Fact]
    public async Task DefaultResultReadsFreshRoutineSlot()
    {
        Assert.Equal(["", "99"], await Run("PUBLIC RESULT=99\nF()\nPRINT RESULT\n"
            + "PROCEDURE F(x=RESULT)\nPRINT x\nFEND"));
    }

    [Fact]
    public async Task MissingHashTableDefaultDoesNotCopyBackIntoEarlierFormal()
    {
        Assert.Equal(["1", "1"], await Run("HASHTBL seed\nseed[\"key\"]=1\nF(seed)\n"
            + "PRINT seed[\"key\"]\nPROCEDURE F(a[],VAR b[]=a)\n"
            + "b[\"key\"]=9\nPRINT a[\"key\"]\nFEND"));
    }

    [Fact]
    public async Task CancellationBeforeDefaultKeepsOriginalTokenAndSkipsBody()
    {
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("F(Tick())\nPROCEDURE F(x,VAR y=3)\n"
                + "PRINT \"body\"\nFEND\nFUNCTION Tick()\n"
                + "PRINT \"trigger\"\nRESULT=1\nFEND", text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["trigger"], output);
    }

    [Fact]
    public async Task ReusedProgramKeepsDefaultSnapshotsIndependent()
    {
        ScriptProgram program = ScriptCompiler.Compile("PUBLIC seed=SPLIT(\"1 2\")\n"
            + "F()\nF()\nPRINT JOIN(seed,\",\")\nPROCEDURE F(VAR x[]=seed)\n"
            + "PRINT JOIN(x,\",\")\nx[0]=9\nFEND");
        var runner = new Interpreter();
        List<string> first = [];
        List<string> second = [];
        await Task.WhenAll(
            runner.RunAsync(program, new CallbackScriptHost(first.Add)),
            runner.RunAsync(program, new CallbackScriptHost(second.Add)));
        Assert.Equal(["1,2", "1,2", "1,2"], first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("ABS(-1)")]
    [InlineData("Unknown()")]
    [InlineData("object.Member")]
    public void CallsInDefaultValuesRemainExplicitCompileErrors(string expression)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(
            "PROCEDURE F(x=" + expression + ")\nFEND"));
    }
}
