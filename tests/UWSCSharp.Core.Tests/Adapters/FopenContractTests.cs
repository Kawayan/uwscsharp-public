using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FopenContractTests
{
    [Theory]
    [InlineData("FOPEN()")]
    [InlineData("FOPEN(,Tick())")]
    [InlineData("FOPEN(Tick(),Tick(),Tick())")]
    [InlineData("FCLOSE(Tick(),Tick(),Tick())")]
    [InlineData("FGET(Tick(),Tick(),Tick(),Tick(),Tick())")]
    [InlineData("FPUT(Tick(),Tick(),Tick(),Tick(),Tick(),Tick())")]
    [InlineData("FDELLINE(Tick(),Tick(),Tick())")]
    [InlineData("READINI(Tick(),Tick(),Tick(),Tick())")]
    [InlineData("WRITEINI(Tick(),Tick(),Tick(),Tick(),Tick())")]
    [InlineData("DELETEINI(Tick(),Tick(),Tick(),Tick())")]
    [InlineData("GETDIR(Tick(),Tick(),Tick(),Tick(),Tick())")]
    [InlineData("DELETEFILE(Tick(),Tick())")]
    public async Task SharedSignaturesPreventInvalidCallEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("SPLIT(\"x\")")]
    [InlineData("\"x\"+CHR(0)")]
    [InlineData("\"x\",0")]
    [InlineData("\"x\",\"$2\"")]
    [InlineData("\"x\",2147483648")]
    [InlineData("\"x\",F_EXISTS OR F_READ")]
    [InlineData("\"x\",F_WRITE8 OR F_WRITE16")]
    public async Task InvalidValuesCarryCallLine(string arguments)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("// 見出し\nFOPEN(" + arguments + ")"));
        Assert.Equal(2, error.Line);
    }
}
