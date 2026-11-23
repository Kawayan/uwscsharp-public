using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ChrContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task CharacterConversionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT CHR(\"6.5e1\")\nPRINT CHR(\" 65 \")\n"
                + "TRY\nPRINT CHR(\"65,0\")\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
                + "TRY\nPRINT CHR(\"$41\")\nEXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["A", "A", "caught", "caught"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["A", "A", "caught", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanTrueIsNegativeAndFalseIsZero()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("code","65")
            TRY
                PRINT CHR(o.Exists("code"))
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT CHR(o.Exists("missing"))
            PRINT CHR(o.Item("code"))
            TRY
                PRINT CHR(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["caught", "\0", "A", "caught", "1"], output);
    }
}
