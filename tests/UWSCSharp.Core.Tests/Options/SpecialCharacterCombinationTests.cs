using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class SpecialCharacterCombinationTests
{
    private const string Body = """

        CONST marker="<#TAB>"
        PUBLIC text="<#CR>"
        PRINT LENGTH(marker)
        PRINT LENGTH(text)
        PRINT F()
        PRINT "a"="A"
        FUNCTION F(value="<#DBL>")
            RESULT=LENGTH(value)
        FEND
        """;

    public static IEnumerable<object[]> OptionOrders()
    {
        foreach (string[] names in Permutations(
            ["SPECIALCHAR", "EXPLICIT", "SAMESTR", "OPTFINALLY"]))
        {
            yield return ["OPTION " + string.Join(',', names)];
        }
    }

    [Theory]
    [MemberData(nameof(OptionOrders))]
    public async Task AllLanguageOptionOrdersPreserveDeclarationAndRoutineLiterals(
        string header)
    {
        var host = new LogHost();
        await new Interpreter().RunAsync(header + Body, host);
        Assert.Equal(["6", "5", "6", "0"], host.Output);
        Assert.Empty(host.Settings);
    }

    [Theory]
    [InlineData("OPTION LOGPATH=\"<#TAB>\",SPECIALCHAR,LOGLINES=2,LOGFILE=6,EXPLICIT,SAMESTR,OPTFINALLY")]
    [InlineData("OPTION LOGPATH=\"<#TAB>\"\nOPTION LOGFILE=6,LOGLINES=2\nOPTION SPECIALCHAR,EXPLICIT,SAMESTR,OPTFINALLY")]
    [InlineData("OPTION SPECIALCHAR,EXPLICIT,SAMESTR,OPTFINALLY\nOPTION LOGPATH=\"<#TAB>\",LOGLINES=2,LOGFILE=6")]
    public async Task LeadingLogOptionsUseFinalLiteralPolicyBeforeInitialization(
        string header)
    {
        var host = new LogHost();
        await new Interpreter().RunAsync(header + Body, host);
        Assert.Equal(["6", "5", "6", "0"], host.Output);
        Assert.Equal(3, host.Settings.Count);
        Assert.Contains(new ScriptLogSettings(Path: "<#TAB>"), host.Settings);
        Assert.Contains(new ScriptLogSettings(MaximumLines: 2), host.Settings);
        Assert.Contains(new ScriptLogSettings(FileMode: 6), host.Settings);
    }

    [Fact]
    public async Task CombinedFlagsPreserveFinallyLiteralAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync("""
                OPTION EXPLICIT,SAMESTR,OPTFINALLY,SPECIALCHAR
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
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["trigger", "<#TAB>"], output);
    }

    private static IEnumerable<string[]> Permutations(string[] names)
    {
        if (names.Length == 0)
        {
            yield return [];
            yield break;
        }

        for (int index = 0; index < names.Length; index++)
        {
            string[] remaining =
                [.. names.Where((_, position) => position != index)];
            foreach (string[] suffix in Permutations(remaining))
            {
                yield return [names[index], .. suffix];
            }
        }
    }

    private sealed class LogHost : IScriptLogHost
    {
        internal List<string> Output { get; } = [];

        internal List<ScriptLogSettings> Settings { get; } = [];

        public ValueTask ConfigureLogAsync(ScriptLogSettings settings,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Settings.Add(settings);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.CompletedTask;
    }
}
