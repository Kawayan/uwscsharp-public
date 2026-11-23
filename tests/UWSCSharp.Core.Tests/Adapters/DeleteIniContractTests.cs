using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class DeleteIniContractTests
{
    [Theory]
    [InlineData("DELETEINI()")]
    [InlineData("DELETEINI(,Tick())")]
    [InlineData("DELETEINI(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"x\"\nFEND"));
    }

    [Theory]
    [InlineData("\" \",\"k\"")]
    [InlineData("\"s\",\" \"")]
    [InlineData("\"s\",\"#x\"")]
    [InlineData("\"s\",\"[x\"")]
    [InlineData("\"s\",\"k\"+CHR(0)")]
    [InlineData("NULL,\"k\"")]
    [InlineData("\"s\",SPLIT(\"x\")")]
    public async Task InvalidFieldsCarryTheCallLine(string fields)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nDELETEINI(" + fields + ",\"x.ini\")"));
        Assert.Equal(2, error.Line);
    }
}
