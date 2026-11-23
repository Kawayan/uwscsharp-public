using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class LengthContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task LengthIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT LENGTH(\"A😀BC\")\nPRINT LENGTH(-12.5)";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["4", "5"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["4", "5"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanArraysAndEnumerationKeepTheirDistinctCounts()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("first","abc")
            o.Add("second","😀")
            PRINT LENGTH(o.Exists("first"))
            PRINT LENGTH(o.Exists("missing"))
            PRINT LENGTH(o)
            PRINT LENGTH(o.Items())
            PRINT GETOLEITEM(o)
            PRINT ALL_OLE_ITEM[0]
            PRINT LENGTH(o)
            PRINT ALL_OLE_ITEM[0]
            o.RemoveAll()
            PRINT LENGTH(o)
            """, output.Add);
        Assert.Equal(["2", "1", "2", "2", "2", "first", "2", "first", "0"], output);
    }
}
