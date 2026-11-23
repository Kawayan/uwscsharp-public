using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class WriteIniContractTests
{
    [Theory]
    [InlineData("WRITEINI()")]
    [InlineData("WRITEINI(Tick(),Tick())")]
    [InlineData("WRITEINI(,Tick(),Tick())")]
    [InlineData("WRITEINI(Tick(),,Tick())")]
    [InlineData("WRITEINI(Tick(),Tick(),)")]
    [InlineData("WRITEINI(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task BadArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"x\"\nFEND"));
    }

    [Theory]
    [InlineData("\"s\",\";x\",\"v\"")]
    [InlineData("\"s\",\" #x\",\"v\"")]
    [InlineData("\"s\",\"[x\",\"]\"")]
    [InlineData("\" \",\"k\",\"v\"")]
    [InlineData("\"s\",\"k\",\"a\"+CHR(0)")]
    [InlineData("\"s\",\"k\",NULL")]
    [InlineData("\"s\",\"k\",SPLIT(\"x\")")]
    public async Task InvalidFieldsCarryTheCallLine(string fields)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nWRITEINI(" + fields + ",\"x.ini\")"));
        Assert.Equal(2, error.Line);
    }
}
