using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class PositionContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task PositionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT POS(\"σ\",\"ς\")\nPRINT POS(\"i\",\"İ\")\n"
                + "PRINT POS(\"I\",\"i\")\nPRINT POS(\"i\",\"ı\")\nTRY\n"
                + "PRINT POS(\"a\",\"a\",\"$1\")\nEXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["1", "0", "1", "0", "caught"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["1", "0", "1", "0", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedNeedleSourceAndOccurrence()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","abc")
            PRINT POS(o.Exists("text"),"TRUE-11")
            PRINT POS("-1",o.Exists("text"))
            PRINT POS("a","aba",o.Exists("text"))
            PRINT POS("a","aba",o.Exists("missing"))
            PRINT POS(o.Item("text"),"ABCabc")
            TRY
                PRINT POS(o,"x")
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["5", "1", "3", "1", "1", "caught", "1"], output);
    }
}
