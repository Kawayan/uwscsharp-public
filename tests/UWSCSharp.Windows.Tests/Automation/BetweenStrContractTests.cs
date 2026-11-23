using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class BetweenStrContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task BothDirectionsUseCultureIndependentTextAndComparison(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT BETWEENSTR(1.5,\".\")\n"
                + "PRINT BETWEENSTR(\"𐐨x]\",\"𐐀\",\"]\")\n"
                + "PRINT BETWEENSTR(\"𐐨x]\",\"𐐀\",\"]\",-1)\n"
                + "PRINT BETWEENSTR(\"aBodyc\",\"A\",\"C\",-1)";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["5", "x", "x", "Body"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["5", "x", "x", "Body"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedTextAndNegativeOccurrence()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            PRINT BETWEENSTR(o.Exists("x"))
            PRINT BETWEENSTR("A1CA2C","A","C",o.Exists("x"))
            PRINT BETWEENSTR("p-1BODYq",o.Exists("x"),"q")
            PRINT BETWEENSTR("A1A2A3A","A","A",2,o.Exists("x"))
            PRINT BETWEENSTR("A1A2A3A","A","A",2,o.Exists("missing"))
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-1", "2", "BODY", "2", "3", "1"], output);
    }

    [Fact]
    public async Task NativeBstrKeepsNulAndObjectIsNotImplicitlyConverted()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","A"+CHR(0)+"😀B")
            PRINT BETWEENSTR(o.Item("text"),"A","B",-1)
            TRY
                PRINT BETWEENSTR(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["\0😀", "caught", "1"], output);
    }
}
