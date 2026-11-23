using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class LogNConversionTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task BothArgumentsUseTheInvariantGrammarWithAndWithoutWindows(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            IWindowsAutomation?[] adapters = [null, new WindowsAutomation()];
            foreach (IWindowsAutomation? adapter in adapters)
            {
                List<string> output = [];
                await new Interpreter(windows: adapter).RunAsync(
                    "PRINT LOGN(\"2\",\"1.9\")\nPRINT LOGN(\".5\",\"8\")\n"
                    + "PRINT LOGN(\"1E2\",\"1E4\")\n"
                    + "TRY\nPRINT LOGN(\"1,9\",2)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
                    + "TRY\nPRINT LOGN(2,\"1,9\")\nEXCEPT\nPRINT \"caught\"\nENDTRY",
                    output.Add);
                Assert.Equal(["0.925999418556223", "-3", "2", "caught", "caught"], output);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleansAreOutsideBothDomainsWhileLiteralTrueIsAUnitArgument()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "o=CREATEOLEOBJ(\"Scripting.Dictionary\")\no.Add(\"x\",1)\n"
            + "TRY\nPRINT LOGN(o.Exists(\"x\"),1)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(2,o.Exists(\"x\"))\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(o.Exists(\"missing\"),1)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(2,o.Exists(\"missing\"))\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(TRUE,1)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(FALSE,1)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "PRINT LOGN(2,TRUE)\nPRINT LOGN(.5,TRUE)\n"
            + "TRY\nPRINT LOGN(2,FALSE)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(EMPTY,1)\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + "TRY\nPRINT LOGN(2,EMPTY)\nEXCEPT\nPRINT \"caught\"\nENDTRY",
            output.Add);
        Assert.Equal(["caught", "caught", "caught", "caught", "caught", "caught",
            "0", "-0", "caught", "caught", "caught"], output);
    }
}
