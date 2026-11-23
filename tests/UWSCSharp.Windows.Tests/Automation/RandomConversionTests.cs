using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RandomConversionTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task BoundConversionIsIndependentOfCultureAndAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "PRINT RANDOM(\"1.9\")\nPRINT RANDOM(\"1E2\")\n"
                + "TRY\nRANDOM(\"1,9\")\nEXCEPT\nPRINT \"caught\"\nENDTRY";
            List<string> output = [];
            await new Interpreter(new UwscRandom(0)).RunAsync(source, output.Add);
            Assert.Equal(["0", "3", "caught"], output);
            output.Clear();
            await new Interpreter(new UwscRandom(0), windows: new WindowsAutomation())
                .RunAsync(source, output.Add);
            Assert.Equal(["0", "3", "caught"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesSignedBoundAndConsumesOneStep()
    {
        var random = new UwscRandom(1);
        List<string> output = [];
        await new Interpreter(random, windows: new WindowsAutomation()).RunAsync(
            "o=CREATEOLEOBJ(\"Scripting.Dictionary\")\no.Add(\"x\",1)\n"
            + "PRINT RANDOM(o.Exists(\"x\"))\n"
            + "PRINT RANDOM(o.Exists(\"missing\"))\nPRINT RANDOM(TRUE)", output.Add);
        Assert.Equal(["134775813", "0", "0"], output);
        Assert.Equal(1172187917.0 / 4294967296.0, random.Next());
    }
}
