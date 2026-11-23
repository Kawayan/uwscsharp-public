using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class JumpLiteralTests
{
    [Theory]
    [InlineData("$2")]
    [InlineData("0x2")]
    [InlineData("X2")]
    [InlineData("+$0002")]
    public async Task HexadecimalLevelUnwindsTwoLoops(string level)
    {
        Assert.Equal(["inner", "outer", "1"], await Run(
            "FOR i=1 TO 2\nTRY\nWHILE TRUE\nTRY\nBREAK " + level
            + "\nFINALLY\nPRINT \"inner\"\nENDTRY\nWEND\nFINALLY\n"
            + "PRINT \"outer\"\nENDTRY\nNEXT\nPRINT i"));
    }

    [Fact]
    public async Task HexadecimalContinueUpdatesOuterFor()
    {
        Assert.Equal(["1", "2", "3"], await Run(
            "FOR i=1 TO 2\nWHILE TRUE\nTRY\nCONTINUE $2\nFINALLY\nPRINT i\nENDTRY\nWEND\nNEXT\nPRINT i"));
    }

    [Theory]
    [InlineData("$0")]
    [InlineData("-$1")]
    [InlineData("$FFFFFFFF")]
    [InlineData("$100000000")]
    [InlineData("$3")]
    [InlineData("count")]
    [InlineData("1+1")]
    public void InvalidOrUnsupportedLevelIsRejected(string level)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(
            "WHILE TRUE\nWHILE TRUE\nBREAK " + level + "\nWEND\nWEND"));
    }
}
