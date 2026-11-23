using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FcloseContractTests
{
    [Theory]
    [InlineData("FCLOSE()")]
    [InlineData("FCLOSE(,Tick())")]
    [InlineData("FCLOSE(Tick(),Tick(),Tick())")]
    public async Task InvalidShapePreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("2147483648,TRUE")]
    [InlineData("-2147483649,TRUE")]
    [InlineData("NULL,TRUE")]
    [InlineData("SPLIT(\"x\"),TRUE")]
    [InlineData("1,NULL")]
    [InlineData("1,\"NaN\"")]
    [InlineData("\"$1\",TRUE")]
    public async Task InvalidValuesCarryCallLine(string arguments)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("// 見出し\nFCLOSE(" + arguments + ")"));
        Assert.Equal(2, error.Line);
    }
}
