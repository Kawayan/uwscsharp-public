using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SetClearContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FillDoesNotInterpretNumericTextOrDependOnCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
                DIM a[]=1,2,3
                SETCLEAR(a," 1,5e+2 ")
                PRINT JOIN(a,"|")
                SETCLEAR(a)
                PRINT JOIN(a,"|")
                """, output.Add);
            Assert.Equal([" 1,5e+2 | 1,5e+2 | 1,5e+2 ", "||"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanKeepsItsVariantTagThroughFillAndRoundTrip()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=1,2,3
            b=a
            SETCLEAR(a,o.Exists("x"))
            PRINT JOIN(a,",")
            PRINT INT(a[0])
            o.Add("values",a)
            copied=o.Item("values")
            PRINT JOIN(copied,",")
            PRINT INT(copied[1])
            PRINT JOIN(b,",")
            """, output.Add);
        Assert.Equal(["-1,-1,-1", "-1", "-1,-1,-1", "-1", "1,2,3"], output);
    }

    [Fact]
    public async Task SharedComObjectsAreNotDeepClonedOrImplicitlyConverted()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            item=CREATEOLEOBJ("Scripting.Dictionary")
            item.Add("first",1)
            DIM a[]=1,2
            SETCLEAR(a,item)
            a[0].Add("second",2)
            PRINT a[1].Count
            PRINT item.Count
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("values",a)
            copied=o.Item("values")
            copied[1].Add("third",3)
            PRINT item.Count
            PRINT copied[0].Count
            """, output.Add);
        Assert.Equal(["2", "2", "3", "3"], output);
    }
}
