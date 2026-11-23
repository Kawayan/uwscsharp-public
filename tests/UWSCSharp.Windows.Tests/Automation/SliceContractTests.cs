using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SliceContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteBoundsDoNotDependOnCultureOrAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "DIM a[]=1,2,3\n"
                + "PRINT JOIN(SLICE(a,\"1.9\",\"99.9\"),\"|\")\n"
                + "PRINT JOIN(SLICE(a,-1e20,1e20),\"|\")\n"
                + "PRINT LENGTH(SLICE(a,1e20))";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["2|3", "1|2|3", "0"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["2|3", "1|2|3", "0"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanBoundsAndElementPayloadRemainSigned()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=1,2,3
            PRINT JOIN(SLICE(a,o.Exists("x")),"|")
            PRINT LENGTH(SLICE(a,0,o.Exists("x")))
            DIM values[]=o.Exists("x"),EMPTY,"a"+CHR(0)+"😀"
            copied=SLICE(values)
            PRINT INT(copied[0])
            PRINT JOIN(copied,"|")
            copied[0]=99
            PRINT INT(values[0])
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["1|2|3", "0", "-1", "-1||a\0😀", "-1", "1"], output);
    }

    [Fact]
    public async Task NativeArrayAndObjectPayloadAreSharedWithinIndependentContainers()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            item=CREATEOLEOBJ("Scripting.Dictionary")
            item.Add("count",1)
            DIM a[]=item,7
            o.Add("parts",a)
            copied=SLICE(o.Item("parts"))
            PRINT copied[0].Count
            copied[0].Add("other",2)
            PRINT item.Count
            copied[1]=99
            PRINT a[1]
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["1", "2", "7", "1"], output);
    }
}
