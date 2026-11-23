using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class CommentTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task JoinedSlashesIgnoreQuotesAndContinuationMarkers(string newline)
    {
        string source = string.Join(newline,
            "PRINT 7/_", "  / \" ; PRINT 99 _",
            "PRINT 8_", "// ignored", "/_", " / \" _",
            "PRINT 9/_", "/");

        Assert.Equal(["7", "8", "9"], await Run(source));
        Assert.Equal([(1, "PRINT 7"), (3, "PRINT 8"), (7, "PRINT 9")],
            Source.Statements(source));
    }

    [Theory]
    [InlineData("// \" ; _\nPRINT 7// \" ; _", "7")]
    [InlineData("PRINT \"https://example.test/a;_\" // \"", "https://example.test/a;_")]
    [InlineData("PRINT \"<#DBL>//<#DBL>\" // \"", "\"//\"")]
    [InlineData("PRINT 8/ _\n2 // comment", "4")]
    [InlineData("PRINT 7_\n// comment\n+2", "9")]
    [InlineData("PRINT 7; // PRINT 9", "7")]
    public async Task CommentsRespectStringsWhitespaceAndStatementBoundaries(
        string source, string expected)
    {
        Assert.Equal([expected], await Run(source));
    }

    [Fact]
    public void CommentCannotCloseAnUnterminatedString()
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\nPRINT \"open // comment"));

        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CommentsDoNotIntroduceDeclarationsOrBlockTerminators()
    {
        Assert.Equal(["7"], await Run("""
            // FUNCTION Invalid(
            IFB TRUE // ENDIF
                PRINT GetValue() // ELSE
            ENDIF // IFB
            FUNCTION GetValue() // "
                RESULT=7 // FEND
            FEND // "
            """));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("cp932")]
    public async Task FilesAndStaticCallsUseTheSameCommentRules(string encoding)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-comments-" + Guid.NewGuid().ToString("N"));
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
                "CALL child // \"\nPRINT 8/_\n / \" _", selected);
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "PRINT \"日本語//\" // \"\nPRINT 7/_\n/ \" _", selected);
            var output = new List<string>();
            var interpreter = new Interpreter();

            await interpreter.RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));

            Assert.Equal(["日本語//", "7", "8"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
