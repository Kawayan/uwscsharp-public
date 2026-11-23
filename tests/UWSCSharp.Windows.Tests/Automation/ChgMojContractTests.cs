using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ChgMojContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ReplacementIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT CHGMOJ(\"σς\",\"σ\",\"x\")\n"
                + "PRINT CHGMOJ(\"İi\",\"i\",\"x\")\nPRINT CHGMOJ(1.5,\".\",\",\")";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["xx", "İx", "1,5"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["xx", "İx", "1,5"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedTextFromAndTo()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","abc")
            PRINT CHGMOJ(o.Exists("text"),"1","x")
            PRINT CHGMOJ("TRUE-11",o.Exists("text"),"x")
            PRINT CHGMOJ("a","a",o.Exists("text"))
            PRINT CHGMOJ(o.Item("text"),"b","x")
            TRY
                PRINT CHGMOJ("","",o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-x", "TRUEx1", "-1", "axc", "caught", "1"], output);
    }
}
