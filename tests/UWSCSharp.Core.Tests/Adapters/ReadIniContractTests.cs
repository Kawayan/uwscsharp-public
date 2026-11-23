using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ReadIniContractTests
{
    [Theory]
    [InlineData("READINI()")]
    [InlineData("READINI(,Tick())")]
    [InlineData("READINI(Tick(),)")]
    [InlineData("READINI(Tick(),Tick(),Tick(),Tick())")]
    public async Task BadArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"x\"\nFEND"));
    }

    [Theory]
    [InlineData("\" \"", "\"x\"")]
    [InlineData("\"s\"", "\" \"")]
    [InlineData("\"s\"+CHR(0)", "\"x\"")]
    [InlineData("\"s\"", "\"x\"+CHR(0)")]
    [InlineData("NULL", "\"x\"")]
    [InlineData("\"s\"", "SPLIT(\"x\")")]
    public async Task InvalidNamesCarryTheCallLine(string section, string key)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nPRINT READINI(" + section + "," + key + ",\"x.ini\")"));
        Assert.Equal(2, error.Line);
    }
}
