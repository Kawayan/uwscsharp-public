using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PublicDeclarationCompletionTests
{
    [Fact]
    public async Task InitializesMultidimensionalAndInferredPublicArraysBeforeMain()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            CONST bound = 1
            PUBLIC matrix[bound, bound] = 1, 2, 3, 4
            PUBLIC words[] = COPY("a,b", 1, 1), COPY("a,b", 2, 2)
            PRINT matrix[1][1]
            PRINT LENGTH(words)
            PRINT words[1]
            """, output.Add);
        Assert.Equal(["4", "2", ",b"], output);
    }

    [Fact]
    public async Task PreservesValuesWithoutInitializersAndReinitializesWhenSpecified()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC shared = 3
            PUBLIC SHARED
            PUBLIC shared = shared + 2
            PUBLIC shared
            PUBLIC items[] = 1, 2
            PUBLIC ITEMS[1]
            PUBLIC items[1] = 3, 4
            PUBLIC items[1]
            PRINT shared
            PRINT items[0]
            PRINT items[1]
            """, output.Add);
        Assert.Equal(["5", "3", "4"], output);
    }

    [Fact]
    public async Task KeepsScalarDeclarationShapeIndependentOfItsStoredValueType()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC items = SPLIT("a,b", ",")
            PUBLIC items
            PRINT LENGTH(items)
            PUBLIC mutable = 1
            PUBLIC mutable = "text"
            PRINT mutable
            """, output.Add);
        Assert.Equal(["2", "text"], output);
    }

    [Theory]
    [InlineData("PUBLIC items[1] = 1, 2\nPUBLIC items[2]")]
    [InlineData("PUBLIC items[1, 1] = 1, 2, 3, 4\nPUBLIC items[3]")]
    [InlineData("PUBLIC items[1, 2]\nPUBLIC items[2, 1]")]
    public async Task RejectsDifferentBoundsOrRankBeforeMain(string declarations)
    {
        ScriptProgram program = ScriptCompiler.Compile(declarations + "\nPRINT 99");
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add)));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData("CONST shared=1\nPUBLIC shared")]
    [InlineData("PUBLIC shared=1\nCONST shared=2")]
    [InlineData("CONST shared=1\nCONST shared=2")]
    [InlineData("PUBLIC shared=1\nPUBLIC shared[1]")]
    [InlineData("PUBLIC shared[1]\nPUBLIC shared=1")]
    [InlineData("PUBLIC HASHTBL shared\nPUBLIC HASHTBL shared")]
    [InlineData("PUBLIC HASHTBL shared\nPUBLIC shared")]
    [InlineData("PUBLIC TRUE=1")]
    [InlineData("PUBLIC LENGTH=1")]
    public void KeepsConstantsBuiltinNamesAndDeclarationKindsStrict(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Theory]
    [InlineData("PUBLIC shared=1\nPUBLIC SHARED")]
    [InlineData("PUBLIC shared[1]\nPUBLIC shared[1]")]
    public void OptPublicRejectsExistingPublicDeclarations(string source)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("OPTION OPTPUBLIC\n" + source));
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void PreservesArrayMetadataAndInitializerComparisonExpressions()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC seed = 1
            PUBLIC items[] = seed, seed = 1
            """);
        GlobalDeclaration array = program.Globals[1];
        Assert.Equal(GlobalDeclarationKind.Array, array.Kind);
        Assert.True(array.Infer);
        Assert.Equal(2, array.Initializers.Count);
        Assert.IsType<BinaryExpr>(array.Initializers[1]);
        Assert.Null(array.Initializer);
    }

    [Fact]
    public async Task SharesRepeatedPublicDeclarationsAcrossCallFiles()
    {
        string directory = NewDirectory();
        try
        {
            string main = Path.Combine(directory, "main.uws");
            File.WriteAllText(main, """
                PUBLIC shared = 1
                PUBLIC items[1] = 4, 5
                CALL child
                PRINT shared
                PRINT items[1]
                """);
            File.WriteAllText(Path.Combine(directory, "child.uws"), """
                PUBLIC shared = shared + 2
                PUBLIC items[1]
                """);
            var output = new List<string>();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));
            Assert.Equal(["3", "5"], output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregatesOptPublicAcrossUnexecutedCallFiles(bool optionInChild)
    {
        string directory = NewDirectory();
        try
        {
            string main = Path.Combine(directory, "main.uws");
            string child = Path.Combine(directory, "child.uws");
            File.WriteAllText(main, (optionInChild ? "" : "OPTION OPTPUBLIC\n")
                + "PUBLIC shared=1\nIF FALSE THEN CALL child");
            File.WriteAllText(child, (optionInChild ? "OPTION OPTPUBLIC\n" : "")
                + "PUBLIC shared=2");
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.CompileFile(main));
            Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReportsTheImportedFileWhenArrayShapeDiffers()
    {
        string directory = NewDirectory();
        try
        {
            string main = Path.Combine(directory, "main.uws");
            string child = Path.Combine(directory, "child.uws");
            File.WriteAllText(main, "PUBLIC items[1]\nCALL child");
            File.WriteAllText(child, "PUBLIC items[2]");
            ScriptProgram program = ScriptCompiler.CompileFile(main);
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(program, new CallbackScriptHost(_ => { })));
            Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
            Assert.Equal(1, error.Line);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"uwsc-public-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
