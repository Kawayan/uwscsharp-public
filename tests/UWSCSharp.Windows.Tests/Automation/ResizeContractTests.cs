using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ResizeContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task QueryAndFiniteTextAreCultureIndependent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
                DIM a[]=1,2,3
                PRINT RESIZE(a, " 4.9e0 ")
                PRINT RESIZE(a,)
                PRINT JOIN(a,",")
                """, output.Add);
            Assert.Equal(["4", "4", "1,2,3,,"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanChangesToEmptyAndArrayRoundTripKeepsSource()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=1,2,3
            o.Add("values",a)
            b=o.Item("values")
            PRINT RESIZE(b,4)
            PRINT JOIN(b,",")
            PRINT JOIN(o.Item("values"),",")
            o.Item("values")=b
            PRINT RESIZE(o.Item("values"))
            PRINT RESIZE(a,o.Exists("x"))
            PRINT LENGTH(a)
            """, output.Add);
        Assert.Equal(["4", "1,2,3,,", "1,2,3", "4", "-1", "0"], output);
    }

    [Fact]
    public async Task ObjectsUnicodeAndNativeBooleanElementsKeepTheirPayload()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            item=CREATEOLEOBJ("Scripting.Dictionary")
            item.Add("first",1)
            DIM a[]=item,"😀"+CHR(0),item.Exists("first")
            b=a
            PRINT RESIZE(a,4)
            a[0].Add("second",2)
            PRINT b[0].Count
            PRINT LENGTHS(a[1])
            PRINT INT(a[2])
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("values",a)
            copied=o.Item("values")
            PRINT RESIZE(copied)
            PRINT copied[0].Count
            PRINT LENGTHS(copied[1])
            PRINT INT(copied[2])
            """, output.Add);
        Assert.Equal(["4", "2", "3", "-1", "4", "2", "3", "-1"], output);
    }
}
