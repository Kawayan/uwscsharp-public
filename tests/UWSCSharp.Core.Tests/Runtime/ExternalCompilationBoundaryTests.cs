using UWSCSharp.Testing;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExternalCompilationBoundaryTests : IDisposable
{
    private readonly TemporaryTestDirectory temporary =
        new("uwsc-compilation-boundary-", "N");

    private string directory => temporary.Path;

    public void Dispose() => temporary.Dispose();

    private string Write(string name, string text)
    {
        string path = Path.Combine(directory, name + ".uws");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ParsesTheMaximumFileChainAndCycleWithoutRecursiveSourceLoading()
    {
        string root = Write("root", "CALL child0");
        for (int index = 0; index < ScriptLimits.MaxExternalScripts - 1; index++)
        {
            string next = index + 1 < ScriptLimits.MaxExternalScripts - 1
                ? $"child{index + 1}" : "root";
            Write($"child{index}", $"PUBLIC value{index}={index}\nCALL {next}");
        }

        ScriptProgram program = ScriptCompiler.CompileFile(root);
        Assert.Equal(128, program.ExternalScripts.Count);
        Assert.Equal(127, program.Globals.Count);
        Assert.All(program.ExternalScripts, script => Assert.NotNull(script.Program));
        Assert.Same(program.ExternalScripts[0],
            Assert.Single(program.ExternalScripts[^1].Dependencies));
    }

    [Fact]
    public void RejectsOneMoreFileWithoutReducingTheExistingLimit()
    {
        string root = Write("root", "CALL child0");
        for (int index = 0; index < ScriptLimits.MaxExternalScripts; index++)
        {
            Write($"child{index}", index + 1 < ScriptLimits.MaxExternalScripts
                ? $"CALL child{index + 1}" : "PRINT 1");
        }

        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(root));
        Assert.Contains("上限", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefersConflictingInheritanceUntilTheTargetPolicyIsKnown(bool explicitPolicy)
    {
        string root = Write("root", "CALL first\nCALL second");
        Write("first", "CALL target");
        string second = Write("second", "OPTION SPECIALCHAR\nCALL target");
        string target = Write("target", explicitPolicy
            ? "OPTION SPECIALCHAR\nPUBLIC value=1" : "PUBLIC value=1");
        if (explicitPolicy)
        {
            ScriptProgram program = ScriptCompiler.CompileFile(root);
            ExternalScript definition = Assert.Single(program.ExternalScripts,
                script => script.Path == target);
            Assert.True(definition.PolicyResolved);
            Assert.True(definition.DeclaresSpecialCharacters);
            Assert.Equal(4, program.ExternalScripts.Count);
        }
        else
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.CompileFile(root));
            Assert.Equal(second, error.Data["UWSCSharp.SourcePath"]);
            Assert.Equal(2, error.Line);
        }
    }
}
