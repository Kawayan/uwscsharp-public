using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ContinuationTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task SplicesTokensAndSkipsCommentOnlyLines(string newline)
    {
        string source = string.Join(newline,
            "longname=9", "PRINT long_", "name",
            "PRINT 1_", "  2", "PRINT 1+_",
            "  // ignored \" ; _", "// another", "2",
            "PRINT \"//;_\"+_", "\"next\"");

        Assert.Equal(["9", "12", "3", "//;_next"],
            await Run(source));
    }

    [Theory]
    [InlineData("PRINT 1 _\n2")]
    [InlineData("PRINT 1+_\n\n2")]
    public void ExplicitWhitespaceAndBlankLinesArePreserved(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Theory]
    [InlineData("// header\nPRINT 1+_", 2)]
    [InlineData("// header\nPRINT 1+_\n// tail", 2)]
    [InlineData("PRINT 1+_\n\"open", 2)]
    public void InvalidContinuationKeepsPhysicalDiagnostic(
        string source, int expectedLine)
    {
        ScriptException error = Assert.Throws<ScriptException>(
            () => ScriptCompiler.Compile(source));

        Assert.Equal(expectedLine, error.Line);
    }

    [Fact]
    public void JoinedStatementsRetainFirstPhysicalLine()
    {
        Assert.Equal([(2, "x=1"), (2, "PRINT 12"), (5, "PRINT 3")],
            Source.Statements("// header\nx=1;PRINT 1_\n// comment\n2\nPRINT 3"));
    }

    [Fact]
    public void StatementEnumerationObservesCancellationBeforeYield()
    {
        using var cancellation = new CancellationTokenSource();
        using IEnumerator<(int Line, string Text)> statements =
            Source.Statements("PRINT 1;PRINT 2", cancellation.Token)
                .GetEnumerator();
        Assert.True(statements.MoveNext());
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => statements.MoveNext());
    }

    [Theory]
    [InlineData("")]
    [InlineData("// comment\n\n_")]
    public void CancelledSourceNeverStartsScanning(string source)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Source.Statements(source, cancellation.Token).ToArray());
    }

    [Fact]
    public async Task ContinuationsComposeWithDeclarationsBlocksAndRoutines()
    {
        Assert.Equal(["3", "ok"], await Run("""
            CONST limit=1+_
                2
            IFB limit=_
                3
                PRINT Add(1,_
                    2)
            ENDIF
            PRINT "o"+_
                "k"
            FUNCTION Add(a,_
                b)
                RESULT=a+_
                    b
            FEND
            """));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("cp932")]
    public async Task FileAndExternalCallShareContinuationRules(string encoding)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
            System.Text.Encoding selected = encoding switch
            {
                "utf8" => new System.Text.UTF8Encoding(false),
                "utf8-bom" => new System.Text.UTF8Encoding(true),
                _ => System.Text.Encoding.GetEncoding(932)
            };
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main,
                "CALL child(1_\n // comment\n 2)\nPRINT Helper()",
                selected);
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "PRINT \"日本\"+_\n // comment\n \"語\"\n"
                + "PRINT PARAM_STR[0]\nFUNCTION Helper()\n"
                + "RESULT=1_\n 2\nFEND", selected);

            var output = new List<string>();
            var interpreter = new Interpreter();
            await interpreter.RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));

            Assert.Equal(["日本語", "12", "12"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
