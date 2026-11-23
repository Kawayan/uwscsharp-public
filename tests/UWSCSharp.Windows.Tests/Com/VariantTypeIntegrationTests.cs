using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 画面・入力は使わず、公開関数とインプロセスCOM辞書の変換・寿命を確認します。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class VariantTypeIntegrationTests
{
    [Fact]
    public async Task FileExistenceCloseAndDeleteReturnLogicalValues()
    {
        string path = Path.GetTempFileName();
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            string name = Path.GetFileName(path);
            string missing = Guid.NewGuid().ToString("N");
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(),
                files: ScriptFiles.ForScript(Path.Combine(directory, "main.uws")))
                .RunAsync($$"""
                    found=FOPEN("{{name}}",F_EXISTS)
                    missing=FOPEN("{{missing}}",F_EXISTS)
                    PRINT VARTYPE(found)
                    PRINT VARTYPE(missing)
                    PRINT found
                    PRINT missing
                    f=FOPEN("{{name}}",F_READ)
                    closed=FCLOSE(f)
                    invalid=FCLOSE(f,TRUE)
                    deleted=DELETEFILE("{{name}}")
                    absent=DELETEFILE("{{name}}")
                    PRINT VARTYPE(closed)
                    PRINT VARTYPE(invalid)
                    PRINT VARTYPE(deleted)
                    PRINT VARTYPE(absent)
                    PRINT closed
                    PRINT invalid
                    PRINT deleted
                    PRINT absent
                    """, output.Add);
            Assert.Equal(["11", "11", "1", "0", "11", "11", "11", "11",
                "1", "0", "1", "0"], output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScriptQueriesAndConversionsKeepTheSourceAndNumericContracts()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            PRINT VARTYPE(EMPTY)
            PRINT VARTYPE(NULL)
            PRINT VARTYPE(NOTHING)
            PRINT VARTYPE(TRUE)
            PRINT VARTYPE(1=1)
            PRINT VARTYPE(TRUE AND TRUE)
            PRINT INT(TRUE)
            source="2.5"
            converted=VARTYPE(source,VAR_INTEGER)
            PRINT converted
            PRINT VARTYPE(converted)
            PRINT VARTYPE(converted+1)
            PRINT source
            PRINT VARTYPE(source)
            PRINT VARTYPE(VARTYPE(source))
            PRINT VARTYPE(123,)
            PRINT VARTYPE("9007199254740993",VAR_INT64)
            PRINT EVAL("VARTYPE(TRUE)")
            """, output.Add);
        Assert.Equal(["0", "1", "9", "11", "11", "5", "1", "2", "3", "5",
            "2.5", "258", "18", "5", "9007199254740993", "11"], output);
    }

    [Fact]
    public async Task ScriptCatchesNativeConversionErrorsAndContinues()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            TRY
                VARTYPE(256,VAR_BYTE)
            EXCEPT
                PRINT "overflow"
            ENDTRY
            TRY
                VARTYPE(NULL,VAR_INTEGER)
            EXCEPT
                PRINT "null"
            ENDTRY
            TRY
                VARTYPE(123,VAR_VARIANT)
            EXCEPT
                PRINT "variant"
            ENDTRY
            PRINT VARTYPE(VARTYPE(-2.5,VAR_INTEGER))
            """, output.Add);
        Assert.Equal(["overflow", "null", "variant", "3"], output);
    }

    [Fact]
    public async Task InterfaceConversionKeepsTheSessionOwnerAndTheDictionary()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            dictionary=CREATEOLEOBJ("Scripting.Dictionary")
            dictionary.Add("value",42)
            dictionary.Add("text","日本語")
            unknown=VARTYPE(dictionary,VAR_UNKNOWN)
            PRINT VARTYPE(unknown)
            dispatch=VARTYPE(unknown,VAR_DISPATCH)
            PRINT VARTYPE(dispatch)
            PRINT dispatch.Item("value")
            PRINT dictionary.Count
            PRINT VARTYPE(dictionary.Item("text"))
            """, output.Add);
        Assert.Equal(["13", "9", "42", "2", "258"], output);
    }

    [Fact]
    public async Task TypedArraysReportTheirElementTypeWithoutCopyingOrConvertingThem()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DIM values[]=1,2
            PRINT VARTYPE(values)
            typed=SAFEARRAY(0,1,17)
            PRINT VARTYPE(typed)
            PRINT VARTYPE(ENCODE("A",CODE_BYTEARRAY))
            PRINT LENGTH(values)
            bytes=ENCODE("AZ",CODE_BYTEARRAY)
            same=VARTYPE(bytes,VARTYPE(bytes))
            PRINT VARTYPE(same)
            PRINT same[0]
            PRINT same[1]
            TRY
                rejected=VARTYPE(bytes,VAR_ARRAY OR VAR_INTEGER)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT VARTYPE(bytes)
            PRINT bytes[1]
            """, output.Add);
        Assert.Equal(["8204", "8209", "8209", "2",
            "8209", "65", "90", "caught", "8209", "90"], output);
    }

    [Fact]
    public async Task PredicateResultsKeepLogicalTypesAndExistingNumericValues()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            PRINT VARTYPE(CHKNUM("123"))
            PRINT VARTYPE(CHKNUM("abc"))
            PRINT VARTYPE(ISUNICODE("Aあ"))
            PRINT VARTYPE(ISUNICODE("😀"))
            PRINT VARTYPE(CHKNUM("123"),VAR_INTEGER)
            PRINT CHKNUM("123")+1
            HASHTBL table
            table["key"]=123
            PRINT VARTYPE(table["key",HASH_EXISTS])
            PRINT VARTYPE(table["absent",HASH_EXISTS])
            PRINT VARTYPE(table["key",HASH_REMOVE])
            PRINT VARTYPE(table["key",HASH_EXISTS])
            PRINT table["key",HASH_EXISTS]
            """, output.Add);
        Assert.Equal(["11", "11", "11", "11", "1", "2", "11", "11", "11", "11", "0"], output);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task UnsupportedOriginalNumericConversionsAreCatchable(int type)
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($$"""
            source=123
            TRY
                VARTYPE(source,{{type}})
                PRINT "converted"
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT source
            """, output.Add);
        Assert.Equal(["rejected", "123"], output);
    }
}
