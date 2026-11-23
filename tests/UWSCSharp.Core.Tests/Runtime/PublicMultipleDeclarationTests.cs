using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PublicMultipleDeclarationTests
{
    [Fact]
    public async Task DeclaresEveryNameWithExplicitVariables()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            PUBLIC first, second
            first = 2
            second = 3
            PRINT first + second
            """, output.Add);
        Assert.Equal(["5"], output);
    }

    [Fact]
    public async Task EvaluatesInitializersInOrderWithoutSplittingFunctionOrStringCommas()
    {
        var output = new List<string>();
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC first = 2, second = first + 3, text = COPY("a,b", 2, 2)
            PRINT first
            PRINT second
            PRINT text
            """);
        Assert.Equal(["first", "second", "text"],
            program.Globals.Select(declaration => declaration.Name));
        Assert.Equal(program.Globals[0].SourceOrder, program.Globals[1].SourceOrder);
        Assert.Equal(program.Globals[0].SourcePath, program.Globals[1].SourcePath);
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));
        Assert.Equal(["2", "5", ",b"], output);
    }

    [Fact]
    public async Task MixesUninitializedArraysScalarsAndAnInitializedArray()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC matrix[1, 1], tail = 7, values[1] = tail, 9
            PRINT matrix[1, 1]
            PRINT tail
            PRINT values[0]
            PRINT values[1]
            """, output.Add);
        Assert.Equal(["", "7", "7", "9"], output);
    }

    [Fact]
    public async Task KeepsInitializedArrayReferencesAndComparisonsAsElements()
    {
        var output = new List<string>();
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC seed = 7
            PUBLIC references[] = 1, seed
            PUBLIC comparisons[] = 2, seed = 7
            PRINT references[1]
            PRINT comparisons[1]
            """);
        Assert.Equal(["seed", "references", "comparisons"],
            program.Globals.Select(declaration => declaration.Name));
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add));
        Assert.Equal(["7", "1"], output);
    }

    [Fact]
    public async Task PreservesRepeatedNamesWithoutOptPublicInTheSameGroup()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC shared = 1, SHARED, shared = shared + 1
            PRINT shared
            """, output.Add);
        Assert.Equal(["2"], output);
    }

    [Fact]
    public async Task CreatesIndependentHashTablesForMultiplePublicNames()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC HASHTBL first, second
            first["key"] = 1
            second["key"] = 2
            PRINT first["key"]
            PRINT second["key"]
            """, output.Add);
        Assert.Equal(["1", "2"], output);
    }

    [Fact]
    public async Task KeepsMultipleModulePublicAndPrivateFieldsInOrder()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PRINT Box.total()
            PRINT Box.second
            MODULE Box
                PUBLIC first = 2, second = first + 3
                DIM hidden = second + 1, other = hidden + 1, values[] = hidden, other
                FUNCTION total()
                    RESULT = first + second + hidden + other + values[1]
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["27", "5"], output);
    }

    [Theory]
    [InlineData("OPTION OPTPUBLIC\nPUBLIC first=1, FIRST")]
    [InlineData("OPTION OPTPUBLIC\nPUBLIC first=1, second=2\nPUBLIC second")]
    [InlineData("CONST fixed=1\nPUBLIC first=2, fixed")]
    [InlineData("PUBLIC first=1, second[1]\nPUBLIC second=2")]
    [InlineData("PUBLIC first,")]
    [InlineData("PUBLIC ,first")]
    [InlineData("PUBLIC first,,second")]
    [InlineData("PUBLIC first=1, second=")]
    public void RejectsDuplicateConflictsAndMalformedGroups(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Theory]
    [InlineData("CONST first=1, second=2")]
    [InlineData("MODULE Box\nCONST first=1, second=2\nENDMODULE")]
    public void KeepsTheExistingSingleConstantDeclarationContract(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public async Task KeepsMultiplePublicInitializationOrderAcrossCallFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"uwsc-public-multiple-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string main = Path.Combine(directory, "main.uws");
            File.WriteAllText(main, """
                PUBLIC first=2, second=first+3
                CALL child
                PRINT third
                PRINT fourth
                """);
            File.WriteAllText(Path.Combine(directory, "child.uws"), """
                PUBLIC third=second+1, fourth=third+1
                """);
            var output = new List<string>();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));
            Assert.Equal(["6", "7"], output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
