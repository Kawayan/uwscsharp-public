using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class RoutineTests
{
    [Fact]
    public async Task FunctionsReturnResultsAndSupportNestedCalls() => Assert.Equal(new[] { "10" }, await Run("PRINT Add(2, Add(3,5))\nFUNCTION Add(a,b)\nRESULT=a+b\nFEND"));

    [Fact]
    public async Task SupportsRecursionAndEarlyExit() => Assert.Equal(new[] { "120" }, await Run("PRINT Factorial(5)\nFUNCTION Factorial(n)\nIF n<=1 THEN\nRESULT=1\nEXIT\nENDIF\nRESULT=n*Factorial(n-1)\nFEND"));

    [Fact]
    public async Task LocalVariablesAndValueParametersDoNotModifyCaller() => Assert.Equal(new[] { "9", "3", "1" }, await Run("x=1\na=3\nChange(a)\nPRINT a\nPRINT x\nPROCEDURE Change(a)\na=9\nx=8\nPRINT a\nFEND"));

    [Fact]
    public async Task MainLocalsAreNotVisibleInRoutines() => Assert.Equal(4, (await Assert.ThrowsAsync<ScriptException>(() => Run("x=1\nF()\nPROCEDURE F()\nPRINT x\nFEND"))).Line);

    [Fact]
    public async Task InitializesGlobalsBeforeMainAndSharesThemWithRoutines() => Assert.Equal(new[] { "2", "3", "10", "3" }, await Run("PRINT counter\nPUBLIC counter=2\nCONST base=7\nIncrement()\nPRINT counter\nPRINT Add()\nPRINT counter\nPROCEDURE Increment()\ncounter=counter+1\nFEND\nFUNCTION Add(value=base+3)\nRESULT=value\nFEND"));

    [Fact]
    public async Task DimAndParametersShadowPublicVariables() => Assert.Equal(new[] { "9", "1" }, await Run("PUBLIC x=1\nLocal()\nPRINT x\nPROCEDURE Local()\nDIM x=9\nPRINT x\nFEND"));

    [Fact]
    public async Task ReferenceArgumentsCopyBackInFormalOrder() => Assert.Equal(new[] { "3" }, await Run("x=1\nUpdate(x,x)\nPRINT x\nPROCEDURE Update(VAR a,VAR b)\na=2\nb=b+2\nFEND"));

    [Fact]
    public async Task ReferenceArgumentDoesNotShareGlobalSlotDuringCall() => Assert.Equal(new[] { "1", "9" }, await Run("PUBLIC x=1\nUpdate(x)\nPRINT x\nPROCEDURE Update(VAR a)\na=9\nPRINT x\nFEND"));

    [Fact]
    public async Task ExitCopiesBackAndFinallyRunsBeforeReturn() => Assert.Equal(new[] { "4", "9" }, await Run("x=1\nPRINT Update(x)\nPRINT x\nFUNCTION Update(VAR a)\nTRY\na=9\nRESULT=4\nEXIT\nFINALLY\nRESULT=RESULT\nENDTRY\nRESULT=100\nFEND"));

    [Fact]
    public async Task ErrorsDoNotCopyBack() => Assert.Equal(
        new[] { "1" }, await Run(
        "x=1\nTRY\nChange(x)\nEXCEPT\nPRINT x\nENDTRY\nPROCEDURE Change(VAR a)\na=9\nPRINT 1/0\nFEND"));

    [Fact]
    public async Task InnerReferenceArgumentCopiesBackToOuterParameter() => Assert.Equal(new[] { "5" }, await Run("x=1\nOuter(x)\nPRINT x\nPROCEDURE Outer(VAR a)\nInner(a)\nFEND\nPROCEDURE Inner(VAR b)\nb=b+4\nFEND"));

    [Fact]
    public async Task DefaultConstantDoesNotReadCallerShadow() => Assert.Equal(new[] { "3" }, await Run("CONST base=3\nDIM base=99\nPRINT F()\nFUNCTION F(value=base)\nRESULT=value\nFEND"));

    [Fact]
    public async Task CancellationInsideRoutineDoesNotCopyBackOrEnterExcept()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new TestHost
        {
            OnOutput = () => cancellation.Cancel()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync(
            "x=1\nTRY\nChange(x)\nEXCEPT\nPRINT 100\nENDTRY\nPROCEDURE Change(VAR a)\na=9\nPRINT a\nPRINT 2\nFEND",
            host, cancellation.Token));
        Assert.Equal(new[] { "9" }, host.Output);
    }

    [Fact]
    public async Task ExitExitStopsCallerAndRunsPendingFinally() => Assert.Equal(new[] { "cleanup" }, await Run("EndAll()\nPRINT 9\nPROCEDURE EndAll()\nTRY\nEXITEXIT\nFINALLY\nPRINT \"cleanup\"\nENDTRY\nFEND"));

    [Fact]
    public async Task ArgumentsEvaluateLeftToRight() => Assert.Equal(new[] { "1", "2", "3" }, await Run("PUBLIC n=0\nPRINT Add(NextValue(),NextValue())\nFUNCTION NextValue()\nn=n+1\nPRINT n\nRESULT=n\nFEND\nFUNCTION Add(a,b)\nRESULT=a+b\nFEND"));

    [Fact]
    public async Task SelectorAndMatchedCaseEvaluateOnce() => Assert.Equal(new[] { "2" }, await Run("PUBLIC n=0\nSELECT Tick()\nCASE Tick(),1\nPRINT n\nCASE Tick()\nPRINT 9\nSELEND\nFUNCTION Tick()\nn=n+1\nRESULT=n\nFEND"));

    [Fact]
    public async Task SupportsDefaultsAndCaseInsensitiveNames() => Assert.Equal(new[] { "5", "9" }, await Run("PRINT add(2)\nPRINT ADD(4,5)\nFUNCTION Add(a,b=3)\nRESULT=a+b\nFEND"));

    [Theory]
    [InlineData("PRINT F()\nFUNCTION F(x)\nFEND", 1)]
    [InlineData("PRINT F(1,2)\nFUNCTION F(x)\nFEND", 1)]
    [InlineData("F(1+2)\nPROCEDURE F(VAR x)\nFEND", 1)]
    [InlineData("CONST x=1\nF(x)\nPROCEDURE F(VAR a)\nFEND", 2)]
    [InlineData("CONST x=1\nx=2", 2)]
    [InlineData("PRINT Missing()", 1)]
    [InlineData("F()\nPROCEDURE F()\nF()\nFEND", 3)]
    public async Task ReportsCallOrBodyErrorLocations(
        string source, int line) => Assert.Equal(line,
        (await Assert.ThrowsAsync<ScriptException>(() => Run(
        source))).Line);

    [Theory]
    [InlineData("FUNCTION F(x=1,y)\nFEND")]
    [InlineData("FUNCTION F(x,X)\nFEND")]
    [InlineData("FUNCTION F(RESULT)\nFEND")]
    [InlineData("FUNCTION F()\nFEND\nPROCEDURE f()\nFEND")]
    [InlineData("FUNCTION F()\nBREAK\nFEND")]
    [InlineData("PROCEDURE F()\nFEND\nPRINT 1")]
    [InlineData("FUNCTION F(x=Unknown())\nFEND")]
    public void RejectsInvalidDefinitions(
        string source) => Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
}
