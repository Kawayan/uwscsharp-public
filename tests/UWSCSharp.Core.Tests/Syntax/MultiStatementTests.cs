using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class MultiStatementTests
{
    [Theory]
    [InlineData("v=1;v=v+2;v=v*3", "9")]
    [InlineData(";; ;v=7;;; ;", "7")]
    [InlineData("v=\"a;b//c_\";v=v+\";d\"", "a;b//c_;d")]
    [InlineData("v=\"<#DBL>;<#DBL>\";v=v+\"x\"", "\";\"x")]
    [InlineData("v=7;// v=9; \" _", "7")]
    [InlineData("v=1;v=v+_\n 2;v=v*3", "9")]
    [InlineData("v=7_\n ;v=v+2", "9")]
    [InlineData("v=0;IF FALSE THEN v=1;v=v+2", "2")]
    [InlineData("v=0;IF TRUE THEN v=1;v=v+2", "3")]
    [InlineData("v=0;IFB FALSE;v=1;ELSE;v=7;ENDIF", "7")]
    [InlineData("v=0;FOR i=1 TO 3;v=v+i;NEXT", "6")]
    [InlineData("v=0;WHILE v<3;v=v+1;WEND", "3")]
    [InlineData("v=0;REPEAT;v=v+1;UNTIL v=3", "3")]
    [InlineData("v=0;SELECT 2;CASE 1;v=1;CASE 2;v=7;SELEND", "7")]
    [InlineData("v=0;TRY;v=7;EXCEPT;v=9;ENDTRY", "7")]
    [InlineData("v=0;TRY;v=7;FINALLY;v=v+2;ENDTRY", "9")]
    [InlineData("v=0;TRY;v=1/0;EXCEPT;v=7;ENDTRY", "7")]
    [InlineData("DIM a[]=1,2;v=a[0]+a[1]", "3")]
    [InlineData("v=\"；\";v=v+\";\"", "；;")]
    public async Task StatementsComposeWithSupportedSyntax(
        string source, string expected)
    {
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            Assert.Equal([expected], await Run(
                source.Replace("\n", newline) + newline + "PRINT v"));
        }
    }

    [Fact]
    public async Task DeclarationsAndRoutineDefinitionsUseStatementBoundaries()
    {
        Assert.Equal(["9"], await Run("""
            OPTION EXPLICIT;CONST c=2;PUBLIC v=1;DIM a[]=3,4
            v=GetValue(a[0]);PRINT v
            FUNCTION GetValue(x);RESULT=x+c+4;FEND
            """));
    }

    [Fact]
    public async Task LoopControlAppliesToTheFollowingStatement()
    {
        Assert.Equal(["4"], await Run("""
            v=0;FOR i=1 TO 5;IF i=2 THEN CONTINUE
            IF i=4 THEN BREAK
            v=v+i;NEXT;PRINT v
            """));
    }

    [Theory]
    [InlineData("; ; \t ;", 0)]
    [InlineData("\u3000;\t; PRINT 7 \u3000; ;", 1)]
    public void EmptyStatementsAndSurroundingWhitespaceAreIgnored(
        string source, int count)
    {
        (int Line, string Text)[] statements = [.. Source.Statements(source)];

        Assert.Equal(count, statements.Length);
        if (count > 0)
        {
            Assert.Equal((1, "PRINT 7"), statements[0]);
        }
    }

    [Fact]
    public void DiagnosticsKeepTheStartingPhysicalLine()
    {
        Assert.Equal([(2, "PRINT 1"), (2, "PRINT 2"), (4, "PRINT 3")],
            Source.Statements("// header\n;PRINT 1;_\nPRINT 2;\nPRINT 3"));
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\nPRINT 1;PRINT ("));

        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task LaterCompileErrorPreventsEarlierOutput()
    {
        var output = new List<string>();
        var interpreter = new Interpreter();

        await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync("PRINT 7;PRINT (",
                new CallbackScriptHost(output.Add)));

        Assert.Empty(output);
    }

    [Fact]
    public void EnumerationObservesCancellationBetweenStatements()
    {
        using var cancellation = new CancellationTokenSource();
        using IEnumerator<(int Line, string Text)> statements =
            Source.Statements("PRINT 1;PRINT 2", cancellation.Token)
                .GetEnumerator();

        Assert.True(statements.MoveNext());
        cancellation.Cancel();
        OperationCanceledException error =
            Assert.Throws<OperationCanceledException>(() => statements.MoveNext());

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("cp932")]
    public async Task FilesAndStaticCallsUseTheSameStatementRules(string encoding)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-multistatement-" + Guid.NewGuid().ToString("N"));
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
                "PRINT \"前;\";CALL child;PRINT \"後\"", selected);
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "PRINT \"日本語;\";PRINT 7; // ignored", selected);
            var output = new List<string>();
            var interpreter = new Interpreter();

            await interpreter.RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));

            Assert.Equal(["前;", "日本語;", "7", "後"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("PRINT 1；PRINT 2")]
    [InlineData("PRINT (1;2)")]
    [InlineData("PRINT \"unclosed;PRINT 7")]
    [InlineData("IFB TRUE;PRINT 7")]
    public void InvalidStatementsAreRejectedAtCompileTime(string source)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\n" + source));

        Assert.Equal(2, error.Line);
    }
}
