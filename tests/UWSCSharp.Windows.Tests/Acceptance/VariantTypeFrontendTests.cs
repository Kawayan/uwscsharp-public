using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class VariantTypeFrontendTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli", false)]
    [InlineData("UWSCSharp.Cli", true)]
    [InlineData("UWSCSharp.Wpf", false)]
    [InlineData("UWSCSharp.Wpf", true)]
    public async Task NormalFrontendRetainsConvertedTypesAcrossThreadCompletion(string frontend, bool worker)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(temporaryRoot,
            "uwsc-vartype-frontend-" + Guid.NewGuid()));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot)
            + Path.DirectorySeparatorChar, directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        try
        {
            string source = $$"""
                PUBLIC converted=0
                PUBLIC arrayType=0
                PUBLIC roundTripType=0
                PUBLIC finished=FALSE
                {{(worker ? "THREAD ConvertValue()" : "ConvertValue()")}}
                WHILE !finished
                    SLEEP(0.01)
                WEND
                PRINT converted
                PRINT VARTYPE(converted)
                PRINT VARTYPE(TRUE)
                PRINT VARTYPE(VARTYPE(converted))
                PRINT VARTYPE(CHKNUM("123"))
                PRINT VARTYPE(ISUNICODE("😀"))
                PRINT VARTYPE(CHKNUM("123"),VAR_INTEGER)
                PRINT VARTYPE(VARTYPE(1,VAR_BOOLEAN),VAR_INTEGER)
                PRINT arrayType
                PRINT roundTripType
                DIM targets[]=14,22,23
                FOR target IN targets
                    TRY
                        VARTYPE(123,target)
                    EXCEPT
                        PRINT "invalid-target"
                    ENDTRY
                NEXT
                TRY
                    VARTYPE(256,VAR_BYTE)
                EXCEPT
                    PRINT "overflow"
                ENDTRY
                EXITEXIT(7)
                PROCEDURE ConvertValue()
                    converted=VARTYPE("9007199254740993",VAR_INT64)
                    typed=SAFEARRAY(-2,-1,VAR_INTEGER)
                    arrayType=VARTYPE(typed)
                    dictionary=CREATEOLEOBJ("Scripting.Dictionary")
                    dictionary.Add("values",typed)
                    roundTripType=VARTYPE(dictionary.Item("values"))
                    finished=TRUE
                FEND
                """;
            _ = ScriptCompiler.CompileWithoutExternalFiles(source);
            Assert.Empty(ScriptCallAnalyzer.Analyze(source).Diagnostics);
            string script = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            (int code, string output) = await SafeArrayFrontendTests.RunAsync(executable, [script]);
            Assert.Equal(7, code);
            Assert.Equal("9007199254740993\n20\n11\n18\n11\n11\n1\n-1\n8195\n8195\n"
                + "invalid-target\ninvalid-target\ninvalid-target\noverflow\n",
                output.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
