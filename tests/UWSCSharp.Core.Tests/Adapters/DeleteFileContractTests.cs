using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class DeleteFileContractTests
{
    [Theory]
    [InlineData("DELETEFILE()")]
    [InlineData("DELETEFILE(,)")]
    [InlineData("DELETEFILE(Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"x\"\nFEND"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("SPLIT(\"x\")")]
    [InlineData("\"x\"+CHR(0)")]
    [InlineData("\"dir*\\x.txt\"")]
    [InlineData("\"x.txt:stream\"")]
    public async Task InvalidPathsCarryTheCallLine(string argument)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("// heading\nPRINT DELETEFILE(" + argument + ")"));
        Assert.Equal(2, error.Line);
    }
}
