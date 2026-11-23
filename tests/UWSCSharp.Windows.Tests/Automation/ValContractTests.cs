using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ValContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ConversionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT VAL(\" 1.9 \")\nPRINT VAL(\"1e2\")\n"
                + "PRINT VAL(\"$FF\")\nPRINT VAL(\"bad\",\"1e2\")\n"
                + "PRINT VAL(\"1,9\",7)";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["1.9", "100", "-999999", "100", "7"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["1.9", "100", "-999999", "100", "7"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedValueForInputAndUsedFallback()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("number","1e3")
            PRINT VAL(o.Item("number"))
            PRINT VAL(o.Exists("number"))
            PRINT VAL(o.Exists("missing"))
            PRINT VAL("bad",o.Exists("number"))
            PRINT VAL("bad",o.Exists("missing"))
            PRINT VAL(TRUE)
            PRINT VAL("bad",TRUE)
            PRINT VAL("2",o)
            TRY
                PRINT VAL(o,7)
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, output.Add);
        Assert.Equal(["1000", "-1", "0", "-1", "0", "1", "1", "2", "caught"], output);
    }
}
