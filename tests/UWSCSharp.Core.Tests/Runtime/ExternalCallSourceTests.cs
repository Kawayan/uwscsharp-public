using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Fact]
    public async Task ImportedRoutineFailureRetainsDefinitionFile()
    {
        string child = Write("library.uws", "PROCEDURE Fail()\nPRINT 1/0\nFEND");
        string main = Write("main.uws", "IF FALSE THEN CALL library\nFail()");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(2, error.Line);
        Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task RootRoutineFailureThroughChildRetainsRootFile()
    {
        Write("child.uws", "Fail()");
        string main = Write("main.uws", "CALL child\nPROCEDURE Fail()\nPRINT 1/0\nFEND");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(3, error.Line);
        Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task NestedImportedRoutineRetainsInnermostDefinitionFile()
    {
        string leaf = Write("sub/leaf.uws", "PROCEDURE Fail()\nIF TRUE THEN PRINT 1/0\nFEND");
        Write("middle.uws", "IF FALSE THEN CALL \"sub/leaf.uws\"\n"
            + "PROCEDURE Relay()\nFail()\nFEND");
        string main = Write("main.uws", "IF FALSE THEN CALL middle\nRelay()");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        File.Delete(leaf);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(program, new CallbackScriptHost(_ => { })));
        Assert.Equal(2, error.Line);
        Assert.Equal(Path.GetFullPath(leaf), error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task ImportedGlobalInitializerRetainsDeclarationFile()
    {
        string child = Write("child.uws", "\nPUBLIC bad=1/0");
        string main = Write("main.uws", "IF FALSE THEN CALL child");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(2, error.Line);
        Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public void DuplicateImportedDefinitionRetainsConflictingFile()
    {
        Write("first.uws", "PROCEDURE Same()\nFEND");
        string second = Write("second.uws", "\nPROCEDURE Same()\nFEND");
        string main = Write("main.uws", "CALL first\nCALL second");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(2, error.Line);
        Assert.Equal(second, error.Data["UWSCSharp.SourcePath"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntaxFailureRetainsFileBeingParsed(bool imported)
    {
        string child = Write("child.uws", "\nIFB TRUE");
        string main = Write("main.uws", imported ? "CALL child" : "\nIFB TRUE");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(imported ? child : main, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public void DuplicateGlobalRetainsConflictingFile()
    {
        Write("first.uws", "PUBLIC shared=1");
        string second = Write("second.uws", "PUBLIC shared=2");
        string main = Write("main.uws", "OPTION OPTPUBLIC\nCALL first\nCALL second");
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(second, error.Data["UWSCSharp.SourcePath"]);
    }

    [Fact]
    public async Task SuppliedArgumentFailureBelongsToCaller()
    {
        Write("child.uws", "PROCEDURE Accept(value)\nFEND");
        string main = Write("main.uws", "IF FALSE THEN CALL child\nAccept(1/0)");
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(_ => { })));
        Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void DisplayDiagnosticIncludesSourceWithoutChangingMessage()
    {
        var error = new ScriptException(3, "test");
        Assert.Equal(error.Message, error.DiagnosticMessage);
        error.Data["UWSCSharp.SourcePath"] = "library.uws";
        Assert.Equal("library.uws: 3行: test", error.DiagnosticMessage);
        Assert.Equal("3行: test", error.Message);
    }
}
