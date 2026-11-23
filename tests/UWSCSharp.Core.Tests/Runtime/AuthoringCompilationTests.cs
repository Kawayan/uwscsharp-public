using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AuthoringCompilationTests
{
    [Fact]
    public void AcceptsSourceWithoutExecutingHostOperations()
    {
        Assert.NotNull(ScriptCompiler.CompileWithoutExternalFiles(
            "EXEC(\"must-not-start.exe\")"));
    }

    [Theory]
    [InlineData("CALL missing_authoring_dependency", 1)]
    [InlineData("IFB FALSE\nCALL missing_authoring_dependency\nENDIF", 2)]
    [InlineData("PROCEDURE sample()\nCALL missing_authoring_dependency\nFEND", 2)]
    public void RejectsExternalCallsBeforeFileAccess(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileWithoutExternalFiles(source));
        Assert.Equal(line, error.Line);
        Assert.Contains("外部CALL", error.Message);
    }

    [Fact]
    public void ReportsSyntaxLine()
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileWithoutExternalFiles("IFB TRUE\nPRINT 1"));
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ScriptCompiler.CompileWithoutExternalFiles("PRINT 1",
                cancellation.Token));
    }
}
