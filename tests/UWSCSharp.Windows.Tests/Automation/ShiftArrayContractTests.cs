using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ShiftArrayContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteTextIsCultureIndependent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(
                "DIM a[]=1,2,3\nSHIFTARRAY(a,\" 1.9 \" )\nPRINT JOIN(a,\",\")", output.Add);
            Assert.Equal([",1,2"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesMinusOneAndArrayRoundTripKeepsOriginal()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=1,2,3
            o.Add("values",a)
            b=o.Item("values")
            SHIFTARRAY(b,o.Exists("x"))
            PRINT JOIN(b,",")
            PRINT JOIN(o.Item("values"),",")
            PRINT JOIN(a,",")
            o.Item("values")=b
            PRINT JOIN(o.Item("values"),",")
            """, output.Add);
        Assert.Equal(["2,3,", "1,2,3", "1,2,3", "2,3,"], output);
    }
}
