using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class MemberEvaluationOrderTests
{
    [Theory]
    [InlineData("Tick()")]
    [InlineData("value:=Tick()")]
    public async Task MissingAdapterIsReportedAfterArgumentEvaluation(
        string arguments)
    {
        List<string> output = [];
        string script = $"PRINT NOTHING.Ping({arguments})\n" + """
            FUNCTION Tick()
                PRINT "argument"
                RESULT=1
            FEND
            """;

        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(script, output.Add));

        Assert.Equal(["argument"], output);
        Assert.Equal(1, error.Line);
        Assert.Contains("Windows操作adapterが設定されていません。", error.Message);
    }
}
