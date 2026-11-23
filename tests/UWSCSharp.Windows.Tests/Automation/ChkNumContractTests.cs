using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ChkNumContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task PredicateIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT CHKNUM(\"1e3\")\n"
                + "PRINT CHKNUM(\" 1.9 \")\nPRINT CHKNUM(\"1,9\")\n"
                + "PRINT CHKNUM(\"$FF\")";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["1", "1", "0", "0"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["1", "1", "0", "0"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeScalarTextAndBooleansDoNotInvokeObjectDefaults()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("number","1e3")
            PRINT CHKNUM(o.Item("number"))
            PRINT CHKNUM(o.Exists("number"))
            PRINT CHKNUM(o.Exists("missing"))
            TRY
                PRINT CHKNUM(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, output.Add);
        Assert.Equal(["1", "1", "1", "caught"], output);
    }
}
