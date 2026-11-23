using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class IsUnicodeContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task Cp932DetectionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT ISUNICODE(\"日本語\")\nPRINT ISUNICODE(\"é\")\n"
                + "PRINT ISUNICODE(\"¥\")\nPRINT ISUNICODE(\"～\")\nPRINT ISUNICODE(1.5)";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["0", "1", "1", "0", "0"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["0", "1", "1", "0", "0"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanAndBstrUseSharedTextWithoutDefaultMemberCalls()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","😀")
            PRINT ISUNICODE(o.Exists("text"))
            PRINT ISUNICODE(o.Exists("missing"))
            PRINT ISUNICODE(o.Item("text"))
            TRY
                PRINT ISUNICODE(o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["0", "0", "1", "caught", "1"], output);
    }
}
