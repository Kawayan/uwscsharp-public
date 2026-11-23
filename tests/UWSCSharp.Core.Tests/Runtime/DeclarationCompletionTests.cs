using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DeclarationCompletionTests
{
    [Fact]
    public async Task DeclaresMultipleNamesWithExplicitVariables()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            DIM first, second
            first = 2
            second = 3
            PRINT first + second
            """, output.Add);
        Assert.Equal(["5"], output);
    }

    [Fact]
    public async Task EvaluatesInitializersInOrderAndKeepsNestedCommas()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            DIM first = tick(1), second = first + tick(2), text = COPY("a,b", 2, 2)
            PRINT first
            PRINT second
            PRINT text
            FUNCTION tick(value)
                PRINT value
                RESULT = value
            FEND
            """, output.Add);
        Assert.Equal(["1", "2", "1", "3", ",b"], output);
    }

    [Fact]
    public async Task DeclaresArraysAndThenScalarsWithoutSplittingBounds()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            DIM grid[1, 1], tail = 7, values[1] = tail, 9
            PRINT LENGTH(grid)
            PRINT grid[1, 1]
            PRINT tail
            PRINT values[0]
            PRINT values[1]
            """, output.Add);
        Assert.Equal(["2", "", "7", "7", "9"], output);
    }

    [Fact]
    public async Task PreservesArrayInitializerReferencesAndComparisons()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            DIM seed = 7
            DIM references[] = 1, seed
            DIM comparisons[] = 2, seed = 7
            PRINT LENGTH(references)
            PRINT references[1]
            PRINT LENGTH(comparisons)
            PRINT comparisons[1]
            """, output.Add);
        Assert.Equal(["2", "7", "2", "1"], output);
    }

    [Fact]
    public async Task KeepsRepeatedDeclarationsEquivalentToSeparateDimStatements()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            DIM shared = 1, SHARED, shared = 3
            PRINT shared
            """, output.Add);
        Assert.Equal(["3"], output);
    }

    [Fact]
    public async Task RunsOnlyTheChosenSingleLineIfDeclarations()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            IF FALSE THEN DIM first = tick(), second = 2 ELSE DIM first = 3, second = 4
            PRINT first
            PRINT second
            FUNCTION tick()
                PRINT "unchosen"
                RESULT = 1
            FEND
            """, output.Add);
        Assert.Equal(["3", "4"], output);
    }

    [Fact]
    public async Task StopsAfterInitializerFailureAndKeepsEarlierDeclaration()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            TRY
                DIM before = 1, broken = 1 / 0, after = 3
            EXCEPT
                PRINT before
            ENDTRY
            TRY
                PRINT after
            EXCEPT
                PRINT "not declared"
            ENDTRY
            """, output.Add);
        Assert.Equal(["1", "not declared"], output);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("PRINT 1\nDIM first = 1, second = 1 / 0",
                _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Theory]
    [InlineData("DIM ,first")]
    [InlineData("DIM first,")]
    [InlineData("DIM first,,second")]
    [InlineData("DIM first =, second")]
    [InlineData("DIM first = 1, second =")]
    [InlineData("DIM first, TRUE")]
    [InlineData("DIM first[1], second(1)")]
    [InlineData("DIM first = COPY(\"a,b\",1,1), second =")]
    public void RejectsMalformedDeclarationsBeforeExecution(string source)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("PRINT 1\n" + source));
        Assert.Equal(2, error.Line);
    }

}
