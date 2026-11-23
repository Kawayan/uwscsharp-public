using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class SpecialCharacterDataTests : IDisposable
{
    private const string Markers = "<#CR><#TAB><#DBL>";
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-special-data-" + Guid.NewGuid());

    public SpecialCharacterDataTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "ini")]
    [InlineData(true, "ini")]
    [InlineData(false, "parameter")]
    [InlineData(true, "parameter")]
    public async Task ExternalMarkersRemainDataAcrossCallAndRoutine(
        bool preserve, string transport)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "payload.txt"),
            Markers + "\r\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "payload.ini"),
            "[S]\r\nv=" + Markers + "\r\n");
        string source = transport switch
        {
            "file" => "DIM f=FOPEN(\"payload.txt\",F_READ)\n"
                + "DIM value=FGET(f,1)\nFCLOSE(f)",
            "ini" => "DIM value=READINI(\"S\",\"v\",\"payload.ini\")",
            _ => "DIM value=PARAM_STR[0]"
        };
        string main = Path.Combine(directory, "main.uws");
        await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"),
            "PRINT PARAM_STR[0]");
        await File.WriteAllTextAsync(main,
            (preserve ? "OPTION SPECIALCHAR,EXPLICIT\n" : "OPTION EXPLICIT\n")
            + source + """

            PRINT value
            CALL child(value)
            PRINT F(value)
            FUNCTION F(x)
                RESULT=x
            FEND
            """);
        var options = new ScriptRunOptions([Markers]) { SourcePath = main };
        var runner = new Interpreter(files: ScriptFiles.ForScript(main));
        List<string> output = [];
        await runner.RunWithExitCodeAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), default, options);
        Assert.Equal([Markers, Markers, Markers], output);
    }
}
