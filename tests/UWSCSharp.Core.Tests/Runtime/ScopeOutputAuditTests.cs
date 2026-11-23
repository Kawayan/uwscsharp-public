using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ScopeOutputAuditTests
{
    [Theory]
    [InlineData("PRINT", "")]
    [InlineData("PRINT EMPTY", "")]
    [InlineData("PRINT NULL", "NULL")]
    [InlineData("PRINT TRUE", "1")]
    [InlineData("PRINT FALSE", "0")]
    [InlineData("PRINT \"a<#CR>b<#TAB>c\"", "a\r\nb\tc")]
    public async Task PrintUsesAdoptedScalarRepresentations(string source, string expected)
    {
        Assert.Equal([expected], await Run(source));
    }

    [Fact]
    public async Task ScopePreservesTypesAndShadowsAcrossFinally()
    {
        Assert.Equal(["2", "4", "2", "1"], await Run("""
            PUBLIC x="2"
            y=1
            Change(x)
            PRINT x
            PRINT y
            PROCEDURE Change(x)
                DIM y="4"
                TRY
                    PRINT x
                    x=3
                    EXIT
                FINALLY
                    PRINT y
                ENDTRY
            FEND
            """));
    }

    [Fact]
    public async Task ConstantsInitializeBeforeMainAndRemainImmutableAfterFailure()
    {
        Assert.Equal(["6", "cleanup", "6"], await Run("""
            PRINT amount
            CONST basis="2"
            CONST amount=basis*3
            TRY
                TRY
                    amount=99
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            EXCEPT
                PRINT amount
            ENDTRY
            """));
    }

    [Fact]
    public async Task RoutineDefaultNumericStringReturnsThroughFinally()
    {
        Assert.Equal(["cleanup", "6"], await Run("""
            PRINT Scale()
            FUNCTION Scale(value="2")
                TRY
                    RESULT=value*3
                    EXIT
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            FEND
            """));
    }

    [Theory]
    [InlineData("PRINT ERROR")]
    public async Task UnsupportedVariantConstantsRemainExplicitFailures(string source)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
        Assert.Equal(1, error.Line);
    }

    [Theory]
    [InlineData("OPTION OPTPUBLIC\nPUBLIC x=1\nPUBLIC X=2")]
    [InlineData("CONST x=1\nCONST X=2")]
    public void UnsupportedDeclarationFormsFailBeforeExecution(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public async Task HostFailureInFinallyPreservesHostException()
    {
        var failure = new InvalidOperationException("output failed");
        var host = new TestHost
        {
            Failure = failure
        };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter().RunAsync("TRY\nPRINT 1/0\nFINALLY\nPRINT NULL\nENDTRY", host));
        Assert.Same(failure, error);
    }
}
