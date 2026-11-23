using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ModuleRuntimeTests
{
    [Fact]
    public async Task InitializesSingletonFieldsBeforeConstructorsAndMain()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            PUBLIC trace = ""
            PRINT First.read()
            PRINT Second.total
            PRINT trace
            MODULE First
                PUBLIC total = 2
                DIM hidden = 3
                CONST increment = 5
                PROCEDURE First
                    trace = trace + "A"
                    total = total + hidden
                FEND
                FUNCTION read()
                    RESULT = THIS.total + hidden + increment
                FEND
            ENDMODULE
            CLASS Second
                PUBLIC total = 7
                PROCEDURE Second()
                    trace = trace + "B"
                    total = total + First.total
                FEND
            ENDCLASS
            """, output.Add);
        Assert.Equal(["13", "12", "AB"], output);
    }

    [Fact]
    public async Task UsesModuleFunctionsAndExplicitGlobalFunctionsWithCallerArguments()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PRINT Box.compute()
            FUNCTION helper(value)
                RESULT = value + 100
            FEND
            MODULE Box
                DIM hidden = 3
                FUNCTION helper(value)
                    RESULT = value + 10
                FEND
                FUNCTION compute()
                    DIM local = 2
                    RESULT = helper(local) + THIS.helper(hidden) + GLOBAL.helper(hidden) + GLOBAL.ABS(-1)
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["129"], output);
    }

    [Fact]
    public async Task KeepsLocalAndParameterShadowingSeparateFromModuleFields()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC shared = 100
            PRINT Box.localValue()
            PRINT Box.parameterValue(8)
            PRINT Box.shared
            PRINT shared
            MODULE Box
                PUBLIC shared = 3
                FUNCTION localValue()
                    DIM shared = 7
                    RESULT = shared + THIS.shared
                FEND
                FUNCTION parameterValue(shared)
                    RESULT = shared + THIS.shared
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["10", "11", "3", "100"], output);
    }

    [Fact]
    public async Task KeepsRoutineConstantsInTheirModuleAndAllowsGlobalConstantShadowing()
    {
        var output = new List<string>();
        ScriptProgram program = ScriptCompiler.Compile("""
            CONST shared = 100
            Box.change()
            PRINT Box.read()
            PRINT Box.shared
            PRINT shared
            MODULE Box
                PUBLIC shared = 3
                PROCEDURE change()
                    shared = shared + 1
                FEND
                FUNCTION read()
                    CONST localConstant = 9
                    RESULT = localConstant
                FEND
            ENDMODULE
            """);
        Assert.DoesNotContain(program.Globals,
            declaration => declaration.Name == "localConstant");
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));
        Assert.Equal(["9", "4", "100"], output);
    }

    [Fact]
    public async Task CopiesNormalAndExplicitVarArgumentsBackToLocalsFieldsAndArrays()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            DIM local = 1
            Box.change(local)
            Box.change(VAR local)
            Box.change(Box.total)
            Box.change(VAR Box.values[0])
            Box.adjust(Box.values)
            PRINT local
            PRINT Box.total
            PRINT Box.values[0]
            PRINT Box.values[1]
            MODULE Box
                PUBLIC total = 7
                PUBLIC values[1] = 4, 5
                PROCEDURE change(VAR value)
                    value = value + 1
                FEND
                PROCEDURE adjust(VAR items[])
                    items[1] = items[0] + items[1]
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["3", "8", "5", "10"], output);
    }

    [Fact]
    public async Task ReevaluatesVarArrayIndexAtCopyOut()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC selected = 0
            Box.change(Box.values[selected])
            PRINT Box.values[0]
            PRINT Box.values[1]
            MODULE Box
                PUBLIC values[1] = 4, 8
                PROCEDURE change(VAR value)
                    selected = 1
                    value = value + 1
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["4", "5"], output);
    }

    [Fact]
    public async Task WritesModuleArrayMembersAndPreservesOtherElements()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            Box.values[1, 0] = 9
            PRINT Box.values[0, 0]
            PRINT Box.values[1][0]
            PRINT Box.values[1, 1]
            MODULE Box
                PUBLIC values[1, 1] = 1, 2, 3, 4
            ENDMODULE
            """, output.Add);
        Assert.Equal(["1", "9", "4"], output);
    }

    [Fact]
    public async Task EvaluatesDefaultsInTheModuleAndKeepsFailedVarCallsUncommitted()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            DIM actual = 2
            PRINT Box.defaultValue()
            TRY
                Box.fail(actual)
            EXCEPT
                PRINT actual
            ENDTRY
            MODULE Box
                PUBLIC seed = 7
                FUNCTION defaultValue(value = seed)
                    RESULT = value
                FEND
                PROCEDURE fail(VAR value)
                    value = 9
                    DIM invalid = 1 / 0
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["7", "2"], output);
    }

    [Theory]
    [InlineData("PRINT Box.hidden")]
    [InlineData("Box.hidden = 3")]
    [InlineData("Box.fixed = 3")]
    [InlineData("Box.change(Box.fixed)")]
    [InlineData("Box.change(VAR Box.hidden)")]
    [InlineData("PRINT THIS.visible")]
    [InlineData("PRINT GLOBAL.visible")]
    public async Task RejectsPrivateAndConstantAccessBeforeMainCanContinue(string action)
    {
        var output = new List<string>();
        string source = action + """

            PRINT "continued"
            MODULE Box
                DIM hidden = 1
                CONST fixed = 2
                PUBLIC visible = 3
                PROCEDURE change(VAR value)
                    value = 4
                FEND
            ENDMODULE
            """;
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(source, output.Add));
        Assert.Empty(output);
    }

    [Fact]
    public async Task ReusingACompiledProgramCreatesFreshModuleState()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            Box.change()
            PRINT Box.total
            MODULE Box
                PUBLIC total = 1
                PROCEDURE change()
                    total = total + 1
                FEND
            ENDMODULE
            """);
        var first = new List<string>();
        var second = new List<string>();
        var interpreter = new Interpreter();
        await interpreter.RunAsync(program, new CallbackScriptHost(first.Add));
        await interpreter.RunAsync(program, new CallbackScriptHost(second.Add));
        Assert.Equal(["2"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task RespectsDeclarationOrderAcrossRootAndModuleFields()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PRINT "unreachable"
            MODULE Box
                PUBLIC value = later
            ENDMODULE
            PUBLIC later = 3
            """);
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync(
            program, new CallbackScriptHost(_ => { })));
    }

    [Fact]
    public async Task RespectsDeclarationOrderForStatementsOnTheSameLine()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PRINT "unreachable"
            MODULE Box; PUBLIC value = later; ENDMODULE; PUBLIC later = 3
            """);
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync(
            program, new CallbackScriptHost(_ => { })));
    }

    [Fact]
    public async Task ConstructorFailurePreventsMainExecution()
    {
        var output = new List<string>();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync("""
            PRINT "main"
            MODULE Box
                PROCEDURE Box()
                    PRINT "constructor"
                    DIM invalid = 1 / 0
                FEND
            ENDMODULE
            """, output.Add));
        Assert.Equal(["constructor"], output);
    }

}
