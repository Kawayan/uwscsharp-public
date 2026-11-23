using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class FormatContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FormattingIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT \"[\"+FORMAT(1.25,\"6\",\"2\")+\"]\"\n"
                + "PRINT FORMAT(\"ab\",\"5\")\nTRY\nPRINT FORMAT(1,\"1,0\")\n"
                + "EXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["[  1.25]", "ababa", "caught"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["[  1.25]", "ababa", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedInputWidthAndPrecision()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("pattern","ab")
            PRINT "["+FORMAT(o.Exists("pattern"),3)+"]"
            PRINT "["+FORMAT(o.Exists("missing"),3)+"]"
            PRINT "["+FORMAT(255,4,o.Exists("pattern"))+"]"
            TRY
                PRINT FORMAT(1,o.Exists("pattern"))
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT FORMAT(o.Item("pattern"),5)
            """, output.Add);
        Assert.Equal(["[ -1]", "[  0]", "[  FF]", "caught", "ababa"], output);
    }
}
