using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ParameterTests
{
    [Fact]
    public async Task PreservesOrderEmptyUnicodeWhitespaceAndNumericText()
    {
        string[] arguments = ["001", "", "日本語", "a b", "a\"b",
            "C:\\path\\", "<#CR>"];
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PRINT LENGTH(PARAM_STR)
            FOR value IN PARAM_STR
                PRINT value
            NEXT
            PRINT PARAM_STR[0] + "x"
            """, output.Add, default, new ScriptRunOptions(arguments));
        Assert.Equal(new[] { "7" }.Concat(arguments).Concat(["001x"]), output);
    }

    [Fact]
    public async Task OldOverloadsExposeEmptyArray()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT LENGTH(PARAM_STR)", output.Add);
        Assert.Equal(["0"], output);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\nPRINT PARAM_STR[0]", _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task RoutinesSeeGlobalParametersAndCannotChangeOptions()
    {
        var options = new ScriptRunOptions(["original"]);
        List<string> output = [];
        await new Interpreter().RunAsync("""
            Change()
            PRINT PARAM_STR[0]
            PROCEDURE Change()
                PRINT param_str[0]
                PARAM_STR[0] = "changed"
            FEND
            """, output.Add, default, options);
        Assert.Equal(["original", "changed"], output);
        Assert.Equal(["original"], options.Arguments);
    }

    [Fact]
    public async Task ReusableProgramAndConcurrentRunsHaveIndependentParameters()
    {
        IScriptRunner runner = new Interpreter();
        ScriptProgram program = ScriptCompiler.Compile("""
            PRINT PARAM_STR[0]
            PARAM_STR[0] = "changed"
            SLEEP(0.01)
            PRINT PARAM_STR[0]
            """);
        List<string> first = [];
        List<string> second = [];
        await Task.WhenAll(
            runner.RunAsync(program, new CallbackScriptHost(first.Add),
                default, new ScriptRunOptions(["a"])),
            runner.RunAsync(program, new CallbackScriptHost(second.Add),
                default, new ScriptRunOptions(["b"])));
        Assert.Equal(["a", "changed"], first);
        Assert.Equal(["b", "changed"], second);
        List<string> third = [];
        await runner.RunAsync(program, new CallbackScriptHost(third.Add),
            default, new ScriptRunOptions(["a"]));
        Assert.Equal(["a", "changed"], third);
    }

    [Fact]
    public void OptionsCopyInputsAndRejectNullEntries()
    {
        string[] input = ["before"];
        var options = new ScriptRunOptions(input);
        input[0] = "after";
        Assert.Equal(["before"], options.Arguments);
        Assert.Throws<ArgumentNullException>(() => new ScriptRunOptions(null!));
        Assert.Throws<ArgumentNullException>(() => new ScriptRunOptions([null!]));
    }

    [Theory]
    [InlineData("PUBLIC PARAM_STR = 1")]
    [InlineData("CONST param_str = 1")]
    public async Task RedeclaringGlobalParameterArrayReportsLine(string source)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + source, _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationAndFailureDoNotLeakParameterChanges()
    {
        var runner = new Interpreter();
        var options = new ScriptRunOptions(["initial"]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("PARAM_STR[0]=\"changed\"\nPRINT 1\nPRINT 2",
                _ => cancellation.Cancel(), cancellation.Token, options));
        await Assert.ThrowsAsync<ScriptException>(() =>
            runner.RunAsync("PARAM_STR[0]=\"changed\"\nPRINT 1/0",
                _ => { }, default, options));
        List<string> output = [];
        await runner.RunAsync("PRINT PARAM_STR[0]", output.Add, default, options);
        Assert.Equal(["initial"], output);
    }

    [Fact]
    public async Task HostOverloadAndStaticParsingDoNotNeedAdapters()
    {
        _ = ScriptCompiler.Compile("PRINT PARAM_STR[0]");
        List<string> output = [];
        IScriptRunner runner = new Interpreter();
        await runner.RunAsync("PRINT PARAM_STR[0]",
            new CallbackScriptHost(output.Add), default,
            new ScriptRunOptions(["host"]));
        Assert.Equal(["host"], output);
    }
}
