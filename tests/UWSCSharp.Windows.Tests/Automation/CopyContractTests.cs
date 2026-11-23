using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class CopyContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task CopyIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT COPY(\"A😀BC\",\" 2e0 \",\"2\")\n"
                + "TRY\nPRINT COPY(\"abc\",\"1,0\")\nEXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["😀B", "caught"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["😀B", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedTextAndLength()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","abc")
            PRINT "["+COPY(o.Exists("text"),1)+"]"
            PRINT "["+COPY("abc",1,o.Exists("text"))+"]"
            PRINT "["+COPY("abc",o.Exists("text"),2)+"]"
            PRINT COPY(o.Item("text"),2)
            """, output.Add);
        Assert.Equal(["[-1]", "[]", "[ab]", "bc"], output);
    }
}
