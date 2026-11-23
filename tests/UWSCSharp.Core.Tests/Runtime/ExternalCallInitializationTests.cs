using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Fact]
    public async Task SiblingPublicInitializersSeeEarlierDeclarations()
    {
        Write("first.uws", "PUBLIC firstValue=1\nPRINT firstValue");
        Write("second.uws", "PUBLIC secondValue=firstValue+1\nPRINT secondValue");
        string main = Write("main.uws", "CALL first\nCALL second\nPRINT secondValue");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["1", "2", "2"], output);
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("CONST")]
    public async Task DescendantsInitializeAfterRootSiblings(string declaration)
    {
        Write("first.uws", "IF FALSE THEN CALL leaf");
        Write("second.uws", $"{declaration} unit=10");
        Write("leaf.uws", $"{declaration} total=unit+1");
        string main = Write("main.uws", "CALL first\nCALL second\nPRINT total");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["11"], output);
    }

    [Fact]
    public async Task DiamondImportsInitializeEachFileOnceInBreadthOrder()
    {
        Write("first.uws", "IF FALSE THEN CALL leaf");
        Write("second.uws", "PUBLIC unit=10\nIF FALSE THEN CALL leaf");
        Write("leaf.uws", "PUBLIC total=unit+1");
        string main = Write("main.uws", "CALL first\nCALL second\nPRINT total");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        List<string> output = [];
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));
        Assert.Equal(["11"], output);
    }

    [Fact]
    public async Task CyclicImportsDoNotInitializeRootTwice()
    {
        Write("child.uws", "PUBLIC total=seed+1\nIF FALSE THEN CALL main");
        string main = Write("main.uws", "PUBLIC seed=2\nCALL child\nPRINT total");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["3"], output);
    }

    [Fact]
    public async Task RootForwardReferenceStillFailsBeforeMainWithRootSource()
    {
        Write("child.uws", "PUBLIC other=2");
        string main = Write("main.uws", "CALL child\nPUBLIC total=other+1\nPRINT total");
        List<string> output = [];
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add)));
        Assert.Equal(2, error.Line);
        Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
        Assert.Empty(output);
    }

    [Fact]
    public async Task ProgramSnapshotAndParallelRunsRetainInitializationOrder()
    {
        string first = Write("first.uws", "IF FALSE THEN CALL leaf");
        Write("second.uws", "PUBLIC unit=10");
        string leaf = Write("leaf.uws", "PUBLIC total=unit+1");
        string main = Write("main.uws", "CALL first\nCALL second\nPRINT total\ntotal=99");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        File.Delete(first);
        File.Delete(leaf);
        List<string> a = [];
        List<string> b = [];
        var interpreter = new Interpreter();
        await Task.WhenAll(interpreter.RunAsync(program, new CallbackScriptHost(a.Add)),
            interpreter.RunAsync(program, new CallbackScriptHost(b.Add)));
        Assert.Equal(["11"], a);
        Assert.Equal(["11"], b);
    }
}
