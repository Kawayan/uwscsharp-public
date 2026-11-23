using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ExplicitOptionTests
{
    [Fact]
    public async Task DeclaredLocalsGlobalsParametersAndResultRemainWritable()
    {
        Assert.Equal(["4", "2"], await Run("""
            OPTION EXPLICIT,SAMESTR,SPECIALCHAR,OPTFINALLY
            PUBLIC total=1
            DIM items[0]=2
            DIM resultValue
            resultValue=Change(items)
            PRINT resultValue
            PRINT total
            FUNCTION Change(VAR values[])
                DIM localValue=values[0]
                values[0]=localValue*2
                total=total+1
                RESULT=values[0]
            FEND
            """));
    }

    [Theory]
    [InlineData("value=1")]
    [InlineData("FOR i=1 TO 2\nNEXT")]
    [InlineData("FOR i IN \"a\"\nNEXT")]
    public async Task UndeclaredWriteFailsAtExecutingStatement(string source)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("OPTION EXPLICIT\n" + source));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task DeclarationFailureRunsFinallyAndDoesNotCreateMisspelledLocal()
    {
        Assert.Equal(["cleanup", "9"], await Run("""
            OPTION EXPLICIT
            DIM count=9
            TRY
                TRY
                    coutn=2
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            EXCEPT
                PRINT count
            ENDTRY
            """));
    }

    [Fact]
    public async Task ParameterShadowAndDefaultRemainIndependentOfCaller()
    {
        Assert.Equal(["6", "1"], await Run("""
            OPTION EXPLICIT
            DIM value=1
            PRINT F()
            PRINT value
            FUNCTION F(value="2")
                RESULT=value*3
            FEND
            """));
    }

    [Fact]
    public async Task OptionDoesNotLeakToNextRun()
    {
        var runner = new Interpreter();
        await runner.RunAsync("OPTION EXPLICIT\nDIM x=1", _ => { });
        var output = new List<string>();
        await runner.RunAsync("x=2\nPRINT x", output.Add);
        Assert.Equal(["2"], output);
    }
}
