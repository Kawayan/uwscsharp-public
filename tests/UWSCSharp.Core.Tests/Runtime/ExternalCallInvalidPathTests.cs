using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(31)]
    public void ControlCharacterInTargetIsRejectedAtCallSite(int character)
    {
        string main = Write("main.uws", "\nCALL \"child"
            + (char)character + ".uws\"");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(2, error.Line);
        Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
        Assert.Contains("CALL", error.Message);
    }
}
