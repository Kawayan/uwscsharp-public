using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class TryInformationBindingTests
{
    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    [InlineData("try_errmsg")]
    [InlineData("try_errline")]
    public async Task ExplicitParameterSurvivesTryResetAndErrorPublication(string name)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("F(\"user\")\nPROCEDURE F(" + name + ")\n"
            + "PRINT " + name + "\nTRY\nPRINT " + name + "\nPRINT 1/0\nEXCEPT\n"
            + "PRINT " + name + "\nENDTRY\nPRINT " + name + "\nFEND", output.Add);
        Assert.Equal(["user", "user", "user", "user"], output);
    }

    [Theory]
    [InlineData("TRY_ERRMSG", "PUBLIC")]
    [InlineData("TRY_ERRLINE", "PUBLIC")]
    [InlineData("TRY_ERRMSG", "CONST")]
    [InlineData("TRY_ERRLINE", "CONST")]
    [InlineData("TRY_ERRMSG", "DIM")]
    [InlineData("TRY_ERRLINE", "DIM")]
    public async Task ExplicitDeclarationIsNotSilentlyHidden(string name, string declaration)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(declaration + " " + name + "=\"user\"\n"
            + "PRINT " + name + "\nTRY\nPRINT 1/0\nEXCEPT\nPRINT " + name
            + "\nENDTRY\nPRINT " + name, output.Add);
        Assert.Equal(["user", "user", "user"], output);
    }

    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    public async Task ReferenceWritebackCreatesExplicitShadow(string name)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("SetInfo(" + name + ")\nPRINT " + name
            + "\nTRY\nPRINT LENGTH(" + name + ")\nEXCEPT\nENDTRY\n"
            + "PROCEDURE SetInfo(VAR a)\na=\"user\"\nFEND", output.Add);
        Assert.Equal(["user", "4"], output);
    }

    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    public async Task DirectWriteCreatesExplicitShadowWithExplicitOption(string name)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("OPTION EXPLICIT\n" + name + "=\"user\"\n"
            + "PRINT " + name + "\nTRY\nPRINT LENGTH(" + name + ")\n"
            + "EXCEPT\nENDTRY", output.Add);
        Assert.Equal(["user", "4"], output);
    }

    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    public async Task ExplicitConstantKeepsReadOnlyProtection(string name)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("CONST " + name + "=\"user\"\nTRY\n"
            + name + "=\"changed\"\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT "
            + name, output.Add);
        Assert.Equal(["caught", "user"], output);
    }

    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    public async Task ReferenceAssignedHashKeepsExistingAssignmentRules(string name)
    {
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("SetInfo(" + name + ")\n" + name
                + "=\"changed\"\nPROCEDURE SetInfo(VAR a)\nHASHTBL h\na=h\nFEND",
                _ => { }));
        Assert.Equal(2, failure.Line);
        Assert.Contains("HASHTBLへの代入", failure.Message);
    }
}
