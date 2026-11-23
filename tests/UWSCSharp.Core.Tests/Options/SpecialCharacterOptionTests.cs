using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class SpecialCharacterOptionTests
{
    [Theory]
    [InlineData("OPTION SPECIALCHAR")]
    [InlineData("option specialchar,specialchar")]
    [InlineData("OPTION OPTFINALLY,SPECIALCHAR")]
    [InlineData("OPTION SPECIALCHAR\nOPTION OPTFINALLY")]
    public async Task OptionPreservesLiteralsInDeclarationsAndRoutineDefaults(string option)
    {
        var output = new List<string>();
        await new Interpreter().RunAsync(option + """

            CONST text="<#CR><#TAB><#DBL>"
            PRINT text
            PRINT F()
            FUNCTION F(value="<#cr><#tab><#dbl>")
                RESULT=value
            FEND
            """, output.Add);
        Assert.Equal(["<#CR><#TAB><#DBL>", "<#cr><#tab><#dbl>"], output);
    }

    [Fact]
    public async Task CombinedOptionPreservesCleanupTextAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                OPTION SPECIALCHAR,OPTFINALLY
                TRY
                    PRINT "trigger"
                FINALLY
                    PRINT "<#TAB>"
                ENDTRY
                """, text =>
            {
                output.Add(text);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Equal(["trigger", "<#TAB>"], output);
    }

    [Fact]
    public async Task CompiledProgramsKeepTheirOwnOptionAcrossParallelRuns()
    {
        ScriptProgram preserved = ScriptCompiler.Compile("OPTION SPECIALCHAR\nPRINT \"<#TAB>\"");
        ScriptProgram converted = ScriptCompiler.Compile("PRINT \"<#TAB>\"");
        var runner = new Interpreter();
        var first = new List<string>();
        var second = new List<string>();
        await Task.WhenAll(runner.RunAsync(preserved, new Output(first)),
            runner.RunAsync(converted, new Output(second)));
        Assert.Equal(["<#TAB>"], first);
        Assert.Equal(["\t"], second);
        first.Clear();
        await runner.RunAsync(preserved, new Output(first));
        Assert.Equal(["<#TAB>"], first);
    }

    [Theory]
    [InlineData("OPTION SPECIALCHAR,")]
    [InlineData("OPTION SPECIALCHAR,UNKNOWN")]
    [InlineData("PRINT 1\nOPTION SPECIALCHAR")]
    public void InvalidOrMisplacedOptionsFailBeforeExecution(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    private sealed class Output(List<string> output) : IScriptHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken cancellationToken)
        {
            output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
