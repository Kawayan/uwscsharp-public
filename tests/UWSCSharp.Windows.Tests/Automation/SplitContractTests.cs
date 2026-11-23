using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SplitContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task SplittingAndNumericModesDoNotDependOnCultureOrAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT JOIN(SPLIT(123.5,\".\"),\"|\")\n"
                + "PRINT JOIN(SPLIT(\" 1,234 |1e3|x\",\"|\",TRUE,TRUE),\"|\")\n"
                + "PRINT JOIN(SPLIT(\"aAxa\",\"a\"),\"|\")";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["123|5", "1234|1000", "|Ax|"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["123|5", "1234|1000", "|Ax|"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanAndBstrUseFiniteTextWithoutDefaultMemberCalls()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","a"+CHR(0)+"😀")
            PRINT JOIN(SPLIT(o.Exists("text")),"|")
            PRINT JOIN(SPLIT(o.Exists("missing")),"|")
            PRINT JOIN(SPLIT(o.Item("text"),CHR(0)),"|")
            TRY
                a=SPLIT(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-1", "0", "a|😀", "caught", "1"], output);
    }

    [Fact]
    public async Task ProducedMixedArrayCanRoundTripThroughCom()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            a=SPLIT("1|x|2","|",FALSE,TRUE)
            o.Add("parts",a)
            b=o.Item("parts")
            PRINT LENGTH(b)
            PRINT b[0]+1
            PRINT "["+b[1]+"]"
            PRINT b[2]+1
            b[0]=99
            PRINT a[0]
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["3", "2", "[]", "3", "1", "1"], output);
    }
}
