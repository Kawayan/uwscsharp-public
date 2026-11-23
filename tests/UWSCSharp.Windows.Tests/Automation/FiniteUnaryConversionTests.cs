using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class FiniteUnaryConversionTests
{
    [Theory]
    [InlineData("INT", "en-US")]
    [InlineData("INT", "de-DE")]
    [InlineData("INT", "ja-JP")]
    [InlineData("INT", "ar-SA")]
    [InlineData("ABS", "en-US")]
    [InlineData("ABS", "de-DE")]
    [InlineData("ABS", "ja-JP")]
    [InlineData("ABS", "ar-SA")]
    [InlineData("ZCUT", "en-US")]
    [InlineData("ZCUT", "de-DE")]
    [InlineData("ZCUT", "ja-JP")]
    [InlineData("ZCUT", "ar-SA")]
    [InlineData("ROUND", "en-US")]
    [InlineData("ROUND", "de-DE")]
    [InlineData("ROUND", "ja-JP")]
    [InlineData("ROUND", "ar-SA")]
    [InlineData("SQRT", "en-US")]
    [InlineData("SQRT", "de-DE")]
    [InlineData("SQRT", "ja-JP")]
    [InlineData("SQRT", "ar-SA")]
    [InlineData("POWER", "en-US")]
    [InlineData("POWER", "de-DE")]
    [InlineData("POWER", "ja-JP")]
    [InlineData("POWER", "ar-SA")]
    [InlineData("SIN", "en-US")]
    [InlineData("SIN", "de-DE")]
    [InlineData("SIN", "ja-JP")]
    [InlineData("SIN", "ar-SA")]
    [InlineData("COS", "en-US")]
    [InlineData("COS", "de-DE")]
    [InlineData("COS", "ja-JP")]
    [InlineData("COS", "ar-SA")]
    [InlineData("TAN", "en-US")]
    [InlineData("TAN", "de-DE")]
    [InlineData("TAN", "ja-JP")]
    [InlineData("TAN", "ar-SA")]
    [InlineData("EXP", "en-US")]
    [InlineData("EXP", "de-DE")]
    [InlineData("EXP", "ja-JP")]
    [InlineData("EXP", "ar-SA")]
    [InlineData("LN", "en-US")]
    [InlineData("LN", "de-DE")]
    [InlineData("LN", "ja-JP")]
    [InlineData("LN", "ar-SA")]
    [InlineData("ARCSIN", "en-US")]
    [InlineData("ARCSIN", "de-DE")]
    [InlineData("ARCSIN", "ja-JP")]
    [InlineData("ARCSIN", "ar-SA")]
    [InlineData("ARCCOS", "en-US")]
    [InlineData("ARCCOS", "de-DE")]
    [InlineData("ARCCOS", "ja-JP")]
    [InlineData("ARCCOS", "ar-SA")]
    [InlineData("ARCTAN", "en-US")]
    [InlineData("ARCTAN", "de-DE")]
    [InlineData("ARCTAN", "ja-JP")]
    [InlineData("ARCTAN", "ar-SA")]
    [InlineData("CEIL", "en-US")]
    [InlineData("CEIL", "de-DE")]
    [InlineData("CEIL", "ja-JP")]
    [InlineData("CEIL", "ar-SA")]
    public async Task ConversionIsIndependentOfCultureAndWindowsAdapter(
        string function, string culture)
    {
        System.Globalization.CultureInfo previous =
            System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo(culture);
            string value = function switch
            {
                "SQRT" or "POWER" => "2.25",
                "ARCSIN" or "ARCCOS" => ".5",
                _ => "1.9"
            };
            string exponentText = function is "ARCSIN" or "ARCCOS" ? "5E-1" : "1E2";
            string suffix = function == "POWER" ? ",\".5\"" : "";
            string script = $"PRINT {function}(\"{value}\"{suffix})\n"
                + $"PRINT {function}(\"{exponentText}\"{suffix})\n"
                + $"TRY\nPRINT {function}(\"1,9\"{suffix})\nEXCEPT\nPRINT \"caught\"\nENDTRY";
            string first = function switch
            {
                "ARCTAN" => "1.0863183977578734",
                "ARCCOS" => "1.0471975511965979",
                "ARCSIN" => "0.5235987755982989",
                "LN" => "0.6418538861723947",
                "EXP" => "6.6858944422792685",
                "TAN" => "-2.9270975146777736",
                "COS" => "-0.32328956686350335",
                "SIN" => "0.9463000876874145",
                "INT" => "1",
                "ROUND" => "2",
                "CEIL" => "2",
                "SQRT" => "1.5",
                "POWER" => "1.5",
                _ => "1.9"
            };
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(
                script, output.Add);
            string second = function is "SQRT" or "POWER" ? "10" : "100";
            if (function == "SIN")
            {
                second = "-0.5063656411097588";
            }

            if (function == "COS")
            {
                second = "0.8623188722876839";
            }

            if (function == "TAN")
            {
                second = "-0.5872139151569291";
            }

            if (function == "EXP")
            {
                second = "2.6881171418161356E+43";
            }

            if (function == "LN")
            {
                second = "4.605170185988092";
            }

            if (function == "ARCSIN")
            {
                second = "0.5235987755982989";
            }

            if (function == "ARCCOS")
            {
                second = "1.0471975511965979";
            }

            if (function == "ARCTAN")
            {
                second = "1.5607966601082315";
            }

            Assert.Equal([first, second, "caught"], output);
            output.Clear();
            await new Interpreter().RunAsync(script, output.Add);
            Assert.Equal([first, second, "caught"], output);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("$FFFFFFFF")]
    [InlineData("1e309")]
    [InlineData("NaN")]
    [InlineData("１.９")]
    [InlineData("1d2")]
    [InlineData("1e")]
    [InlineData(".")]
    public async Task InvalidTextReachesScriptExcept(string text)
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "TRY\nPRINT INT(\"" + text
            + "\")\nEXCEPT\nPRINT \"caught\"\nENDTRY",
            output.Add);
        Assert.Equal(["caught"], output);
    }

    [Fact]
    public async Task NativeBooleanUsesSignedPayloadWithoutChangingLiterals()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "o=CREATEOLEOBJ(\"Scripting.Dictionary\")\no.Add(\"x\",1)\n"
            + "PRINT INT(o.Exists(\"x\"))\n"
            + "PRINT INT(o.Exists(\"missing\"))\nPRINT INT(TRUE)\n"
            + "PRINT ABS(o.Exists(\"x\"))\n"
            + "PRINT ABS(o.Exists(\"missing\"))\nPRINT ABS(TRUE)\n"
            + "PRINT ZCUT(o.Exists(\"x\"))\n"
            + "PRINT ZCUT(o.Exists(\"missing\"))\nPRINT ZCUT(TRUE)\n"
            + "PRINT ROUND(o.Exists(\"x\"))\n"
            + "PRINT ROUND(o.Exists(\"missing\"))\n"
            + "PRINT ROUND(1,o.Exists(\"x\"))\nPRINT ROUND(TRUE)\n"
            + "TRY\nPRINT SQRT(o.Exists(\"x\"))\n"
            + "EXCEPT\nPRINT \"domain\"\nENDTRY\n"
            + "PRINT SQRT(o.Exists(\"missing\"))\nPRINT SQRT(TRUE)\n"
            + "PRINT POWER(o.Exists(\"x\"),3)\n"
            + "PRINT POWER(2,o.Exists(\"x\"))\n"
            + "PRINT POWER(o.Exists(\"missing\"),2)\nPRINT POWER(TRUE,2)\n"
            + "PRINT SIN(o.Exists(\"x\"))\n"
            + "PRINT SIN(o.Exists(\"missing\"))\nPRINT SIN(TRUE)\n"
            + "PRINT COS(o.Exists(\"x\"))\n"
            + "PRINT COS(o.Exists(\"missing\"))\nPRINT COS(TRUE)\n"
            + "PRINT TAN(o.Exists(\"x\"))\n"
            + "PRINT TAN(o.Exists(\"missing\"))\nPRINT TAN(TRUE)\n"
            + "PRINT EXP(o.Exists(\"x\"))\n"
            + "PRINT EXP(o.Exists(\"missing\"))\nPRINT EXP(TRUE)\n"
            + "TRY\nPRINT LN(o.Exists(\"x\"))\n"
            + "EXCEPT\nPRINT \"domain\"\nENDTRY\n"
            + "TRY\nPRINT LN(o.Exists(\"missing\"))\n"
            + "EXCEPT\nPRINT \"domain\"\nENDTRY\nPRINT LN(TRUE)\n"
            + "TRY\nPRINT LN(FALSE)\nEXCEPT\nPRINT \"domain\"\nENDTRY\n"
            + "TRY\nPRINT LN(EMPTY)\nEXCEPT\nPRINT \"domain\"\nENDTRY\n"
            + "PRINT ARCSIN(o.Exists(\"x\"))\n"
            + "PRINT ARCSIN(o.Exists(\"missing\"))\nPRINT ARCSIN(TRUE)\n"
            + "PRINT ARCCOS(o.Exists(\"x\"))\n"
            + "PRINT ARCCOS(o.Exists(\"missing\"))\nPRINT ARCCOS(TRUE)\n"
            + "PRINT ARCTAN(o.Exists(\"x\"))\n"
            + "PRINT ARCTAN(o.Exists(\"missing\"))\nPRINT ARCTAN(TRUE)\n"
            + "PRINT CEIL(o.Exists(\"x\"))\n"
            + "PRINT CEIL(o.Exists(\"missing\"))\nPRINT CEIL(TRUE)",
            output.Add);
        Assert.Equal(["-1", "0", "1", "1", "0", "1", "0", "0", "1",
            "-1", "0", "1", "1", "domain", "0", "1",
            "-1", "0.5", "0", "1", "-0.8414709848078965",
            "0", "0.8414709848078965", "0.5403023058681398",
            "1", "0.5403023058681398", "-1.5574077246549023",
            "0", "1.5574077246549023", "0.36787944117144233",
            "1", "2.718281828459045", "domain", "domain", "0",
            "domain", "domain", "-1.5707963267948966", "0",
            "1.5707963267948966", "3.141592653589793",
            "1.5707963267948966", "0", "-0.7853981633974483",
            "0", "0.7853981633974483", "-1", "0", "1"], output);
    }
}
