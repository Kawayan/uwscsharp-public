using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SameStringOptionTests
{
    [Theory]
    [InlineData("", "1", "1", "xx", "4")]
    [InlineData("OPTION SAMESTR\n", "0", "2", "Ax", "3")]
    public async Task OptionControlsComparisonSearchAndReplacement(string option,
        string equal, string position, string replacement, string reverse)
    {
        Assert.Equal([equal, position, replacement, reverse], await Run(option + """
            PRINT "A"="a"
            PRINT POS("a","Aa")
            PRINT REPLACE("Aa","a","x")
            PRINT POS("a","aAaA",-1)
            """));
    }

    [Theory]
    [InlineData("", "match")]
    [InlineData("OPTION SAMESTR\n", "default")]
    public async Task SelectUsesTheSameProgramSetting(string option, string expected)
    {
        Assert.Equal([expected], await Run(option + """
            SELECT "A"
            CASE "a"
                PRINT "match"
            DEFAULT
                PRINT "default"
            SELEND
            """));
    }

    [Fact]
    public async Task RoutineAndConstantComparisonRespectCombinedOptions()
    {
        Assert.Equal(["0", "Ax", "<#TAB>"], await Run("""
            OPTION SAMESTR,SPECIALCHAR,OPTFINALLY
            CONST equal="A"="a"
            PRINT equal
            PRINT F()
            PRINT "<#TAB>"
            FUNCTION F()
                RESULT=CHGMOJ("Aa","a","x")
            FEND
            """));
    }

    [Fact]
    public async Task SettingDoesNotLeakBetweenProgramsOnOneInterpreter()
    {
        var output = new List<string>();
        var runner = new Interpreter();
        await runner.RunAsync("OPTION SAMESTR\nPRINT \"A\"=\"a\"", output.Add);
        await runner.RunAsync("PRINT \"A\"=\"a\"", output.Add);
        Assert.Equal(["0", "1"], output);
    }
}
