using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class TryInformationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("OPTION EXPLICIT\n")]
    public async Task InitialInformationIsEmptyInEachRoutineAndExecution(string option)
    {
        var runner = new Interpreter();
        string script = option + "PRINT LENGTH(TRY_ERRMSG)\nPRINT LENGTH(TRY_ERRLINE)\n"
            + "ReadInfo()\nPROCEDURE ReadInfo()\n"
            + "PRINT LENGTH(TRY_ERRMSG)\nPRINT LENGTH(TRY_ERRLINE)\nFEND";
        for (int iteration = 0; iteration < 2; iteration++)
        {
            List<string> output = [];
            await runner.RunAsync(script, output.Add);
            Assert.Equal(["0", "0", "0", "0"], output);
        }
    }

    [Theory]
    [InlineData("EXCEPT")]
    [InlineData("FINALLY")]
    public async Task EveryTryClearsPreviousInformation(string handler)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("TRY\nPRINT 1/0\nEXCEPT\n"
            + "PRINT LENGTH(TRY_ERRMSG)>0\nPRINT TRY_ERRLINE\nENDTRY\n"
            + "TRY\nPRINT LENGTH(TRY_ERRMSG)\nPRINT LENGTH(TRY_ERRLINE)\n"
            + handler + "\nENDTRY\nPRINT LENGTH(TRY_ERRMSG)\nPRINT LENGTH(TRY_ERRLINE)",
            output.Add);
        Assert.Equal(["1", "2", "0", "0", "0", "0"], output);
    }

    [Fact]
    public async Task NestedTryInHandlerClearsSameFrameInformation()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("TRY\nPRINT 1/0\nEXCEPT\n"
            + "PRINT LENGTH(TRY_ERRMSG)>0\nTRY\nPRINT LENGTH(TRY_ERRMSG)\n"
            + "EXCEPT\nENDTRY\nPRINT LENGTH(TRY_ERRMSG)\nENDTRY", output.Add);
        Assert.Equal(["1", "0", "0"], output);
    }

    [Fact]
    public async Task FinallySeesPendingScriptErrorAndOuterExceptKeepsItsLine()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("TRY\nTRY\nPRINT 1/0\nFINALLY\n"
            + "PRINT LENGTH(TRY_ERRMSG)>0\nPRINT TRY_ERRLINE\nENDTRY\nEXCEPT\n"
            + "PRINT LENGTH(TRY_ERRMSG)>0\nPRINT TRY_ERRLINE\nENDTRY", output.Add);
        Assert.Equal(["1", "3", "1", "3"], output);
    }

    [Fact]
    public async Task CalleeInformationDoesNotReplaceCallerInformation()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("TRY\nPRINT 1/0\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nReadInfo()\nPRINT TRY_ERRLINE\nENDTRY\n"
            + "PROCEDURE ReadInfo()\nPRINT LENGTH(TRY_ERRMSG)\n"
            + "TRY\nPRINT missing\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\nFEND", output.Add);
        Assert.Equal(["2", "0", "11", "2"], output);
    }
}
