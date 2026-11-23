using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ByteTextLanguageCallContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalledTextBlocksAndShortCircuitOptionsUseExistingImportPolicy(bool dynamic)
    {
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-b2-call-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string main = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), """
                OPTION SHORTCIRCUIT
                TEXTBLOCK imported
                  CALL // _ ; "raw"
                ENDTEXTBLOCK
                PRINT imported
                """);
            string call = dynamic ? "EVAL(\"CALL child\")" : "CALL child";
            await File.WriteAllTextAsync(main, $$"""
                PUBLIC calls=0
                IF FALSE AND ProbeSideEffect() THEN PRINT "bad"
                PRINT calls
                {{call}}
                IF FALSE AND ProbeSideEffect() THEN PRINT "bad"
                PRINT calls
                PRINT imported
                FUNCTION ProbeSideEffect()
                    calls=calls+1
                    RESULT=TRUE
                FEND
                """);
            List<string> output = [];
            await new Interpreter(files: ScriptFiles.ForScript(main)).RunAsync(
                ScriptCompiler.CompileFile(main), new CallbackScriptHost(output.Add));
            Assert.Equal([dynamic ? "1" : "0", "  CALL // _ ; \"raw\"",
                dynamic ? "1" : "0", "  CALL // _ ; \"raw\""], output);
        }
        finally
        {
            // このテストで作成した一時folderだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EncodedBytesSurviveCallAndSourceDiagnosticsKeepChildLines()
    {
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-b2-encoding-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string main = Path.Combine(directory, "main.uws");
            string child = Path.Combine(directory, "child.uws");
            await File.WriteAllTextAsync(main, "PUBLIC encoded=ENCODE(\"あ\",CODE_UTF8)\nCALL child");
            await File.WriteAllTextAsync(child, """
                TEXTBLOCK sample
                //;_
                ENDTEXTBLOCK
                PRINT LENGTHB(encoded)
                PRINT 1/0
                """);
            List<string> output = [];
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter(files: ScriptFiles.ForScript(main)).RunAsync(
                    ScriptCompiler.CompileFile(main), new CallbackScriptHost(output.Add)));
            Assert.Equal(["3"], output);
            Assert.Equal(5, error.Line);
            Assert.Equal(child, error.Data["UWSCSharp.SourcePath"]);
        }
        finally
        {
            // 所有したscriptと出力だけを解放します。
            Directory.Delete(directory, recursive: true);
        }
    }
}
