using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class GetTimeConversionTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ConversionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT GETTIME(\"1.5\",\"2000/02/28\")\n"
                + "PRINT G_TIME_DD2\nPRINT G_TIME_HH2\n"
                + "TRY\nGETTIME(\"1,5\",\"20000101\")\n"
                + "EXCEPT\nPRINT G_TIME_DD2\nENDTRY";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["5140800", "29", "12", "29"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["5140800", "29", "12", "29"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedDaysWithoutChangingLiteralBoolean()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            PRINT GETTIME(o.Exists("x"),"20000102")
            PRINT GETTIME(o.Exists("missing"),"20000102")
            PRINT GETTIME(TRUE,"20000102")
            PRINT G_TIME_DD2
            """, output.Add);
        Assert.Equal(["0", "86400", "172800", "03"], output);
    }
}
