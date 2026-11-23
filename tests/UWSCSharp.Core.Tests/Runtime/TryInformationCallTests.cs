using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Theory]
    [InlineData("TRY_ERRMSG")]
    [InlineData("TRY_ERRLINE")]
    public async Task CallFrameKeepsIndependentAutomaticInformation(string name)
    {
        Write("child.uws", "PRINT LENGTH(" + name + ")\n" + name + "=\"child\"\n"
            + "PRINT " + name + "\nTRY\nPRINT LENGTH(" + name + ")\n"
            + "EXCEPT\nENDTRY");
        string main = Write("main.uws", name + "=\"parent\"\nCALL child\nPRINT " + name);
        var host = new ControlFlowTests.TestHost();
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
        Assert.Equal(["0", "child", "5", "parent"], host.Output);
    }
}
