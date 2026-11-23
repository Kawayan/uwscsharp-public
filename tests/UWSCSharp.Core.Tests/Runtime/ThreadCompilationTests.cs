using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ThreadCompilationTests
{
    [Fact]
    public void PreservesThreadCallsAndSingleLineConditionalMemberCalls()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            THREAD work(1)
            IF TRUE THEN THREAD Box.work(2)
            PROCEDURE work(value)
                PRINT value
            FEND
            MODULE Box
                PROCEDURE work(value)
                    PRINT value
                FEND
            ENDMODULE
            """);
        ThreadStatement first = Assert.IsType<ThreadStatement>(program.Main[0]);
        Assert.IsType<CallExpr>(first.Call);
        IfStatement conditional = Assert.IsType<IfStatement>(program.Main[1]);
        ThreadStatement second = Assert.IsType<ThreadStatement>(
            Assert.Single(Assert.Single(conditional.Branches).Body));
        Assert.IsType<MemberExpr>(second.Call);
        Assert.Equal(first.SourcePath, second.SourcePath);
    }

    [Theory]
    [InlineData("THREAD")]
    [InlineData("THREAD 1")]
    [InlineData("THREAD work")]
    [InlineData("THREAD values[0]")]
    [InlineData("THREAD work()+1")]
    [InlineData("PROCEDURE THREAD()\nFEND")]
    public void RejectsNonCallsAndReservedRoutineNames(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }
}
