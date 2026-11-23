using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FgetContractTests
{
    [Theory]
    [InlineData("FGET()")]
    [InlineData("FGET(Tick())")]
    [InlineData("FGET(,Tick())")]
    [InlineData("FGET(Tick(),)")]
    [InlineData("FGET(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidShapePreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("0,1")]
    [InlineData("1,0")]
    [InlineData("1,-3")]
    [InlineData("1,2147483648")]
    [InlineData("1,1,-1")]
    [InlineData("1,1,1,3")]
    [InlineData("1,F_LINECOUNT,-1,3")]
    [InlineData("NULL,1")]
    [InlineData("1,SPLIT(\"x\")")]
    [InlineData("1,\"$1\"")]
    public async Task InvalidValuesCarryCallLine(string arguments)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("// 見出し\nFGET(" + arguments + ")"));
        Assert.Equal(2, error.Line);
    }
}
