using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class GetDirContractTests
{
    [Theory]
    [InlineData("GETDIR()")]
    [InlineData("GETDIR(,Tick())")]
    [InlineData("GETDIR(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"x\"\nFEND"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"x\"+CHR(0)")]
    [InlineData("NULL")]
    [InlineData("SPLIT(\"x\")")]
    [InlineData("\"x\",\"sub/*.txt\"")]
    [InlineData("\"x\",\"x:y\"")]
    [InlineData("\"x\",\"*\",\"$1\"")]
    [InlineData("\"x\",\"*\",0,5")]
    public async Task InvalidFieldsCarryTheCallLine(string arguments)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nPRINT GETDIR(" + arguments + ")"));
        Assert.Equal(2, error.Line);
    }
}
