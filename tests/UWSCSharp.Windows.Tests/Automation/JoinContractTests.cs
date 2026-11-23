using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class JoinContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ElementAndSeparatorTextDoNotDependOnCultureOrAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "DIM a[]=1.5,-0,EMPTY,\"😀\"\n"
                + "PRINT JOIN(a,\"|\")\nPRINT JOIN(a,1.5,TRUE,0,99)";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["1.5|-0||😀", "1.51.5-01.5😀"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["1.5|-0||😀", "1.51.5-01.5😀"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanAppliesToElementsSeparatorsAndRanges()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x","a"+CHR(0)+"😀")
            DIM a[]=o.Exists("x"),o.Exists("missing"),o.Item("x")
            PRINT JOIN(a,"|")
            PRINT JOIN(a,o.Exists("x"))
            PRINT JOIN(a,"|",TRUE,o.Exists("x"),o.Exists("missing"))
            TRY
                PRINT JOIN(o,"|")
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-1|0|a\0😀", "-1-10-1a\0😀", "-1", "caught", "1"], output);
    }

    [Fact]
    public async Task NativeArrayRoundTripRetainsEmptyTextAndInputIsolation()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            DIM a[]=1,EMPTY,"",2
            o.Add("parts",a)
            b=o.Item("parts")
            PRINT JOIN(b,"|")
            PRINT JOIN(b,"|",TRUE)
            b[0]=99
            PRINT JOIN(a,"|")
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["1|||2", "1|2", "1|||2", "1"], output);
    }
}
