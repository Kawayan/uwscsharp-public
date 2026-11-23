using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TrimContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task TrimmingIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT TRIM(1.5)\nPRINT TRIM(\"　a　\",\" 1e0 \")\n"
                + "PRINT TRIM(\"　a　\",\"0.5\")\nTRY\nPRINT TRIM(\"a\",\"1,0\")\n"
                + "EXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["1.5", "a", "　a　", "caught"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["1.5", "a", "　a　", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedTextAndNonzeroTruncatedFlag()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text"," a ")
            PRINT TRIM(o.Exists("text"))
            PRINT TRIM("　a　",o.Exists("text"))
            PRINT TRIM("　a　",o.Exists("missing"))
            PRINT TRIM(o.Item("text"))
            TRY
                PRINT TRIM(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-1", "a", "　a　", "a", "caught", "1"], output);
    }
}
