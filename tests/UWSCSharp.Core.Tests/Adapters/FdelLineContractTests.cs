using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FdelLineContractTests
{
    [Theory]
    [InlineData("FDELLINE()")]
    [InlineData("FDELLINE(Tick())")]
    [InlineData("FDELLINE(,Tick())")]
    [InlineData("FDELLINE(Tick(),)")]
    [InlineData("FDELLINE(Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("0,1")]
    [InlineData("1,0")]
    [InlineData("1,-1")]
    [InlineData("1,2147483648")]
    [InlineData("1,\"$1\"")]
    [InlineData("NULL,1")]
    [InlineData("1,SPLIT(\"x\")")]
    public async Task InvalidNumbersCarryTheCallLine(string arguments)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nFDELLINE(" + arguments + ")"));
        Assert.Equal(2, error.Line);
    }
}
