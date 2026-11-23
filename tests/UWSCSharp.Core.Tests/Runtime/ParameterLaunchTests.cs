using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ParameterLaunchTests
{
    [Fact]
    public async Task ParsedLaunchResolvesOnlyScriptAndPreservesAllParameterText()
    {
        string[] values = ["日本語/script with spaces.uws", "", "001", "a b",
            "a\"b", "C:\\path\\", "<#CR>", "--verify-runtime"];
        var options = ScriptRunOptions.FromCommandLine(values,
            Path.GetTempPath());
        Assert.Equal(Path.GetFullPath(values[0], Path.GetTempPath()), options.SourcePath);
        string[] expected = [.. values.Skip(1)];
        values[1] = "changed";
        List<string> output = [];
        await new Interpreter().RunAsync("FOR x IN PARAM_STR\nPRINT x\nNEXT",
            output.Add, default, options);
        Assert.Equal(expected, output);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)options.Arguments)[0] = "mutation");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void BlankScriptIsRejectedBeforeExecution(string path)
    {
        Assert.Throws<ArgumentException>(() => ScriptRunOptions.FromCommandLine(
            [path, "parameter"], Path.GetTempPath()));
    }

    [Fact]
    public void MissingAndNullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ScriptRunOptions.FromCommandLine(null!, Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() =>
            ScriptRunOptions.FromCommandLine([], Path.GetTempPath()));
        Assert.Throws<ArgumentNullException>(() =>
            ScriptRunOptions.FromCommandLine([null!], Path.GetTempPath()));
        Assert.Throws<ArgumentNullException>(() =>
            ScriptRunOptions.FromCommandLine(["script.uws", null!], Path.GetTempPath()));
    }
}
