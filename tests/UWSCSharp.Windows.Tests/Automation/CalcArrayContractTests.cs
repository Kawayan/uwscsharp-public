using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class CalcArrayContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ModesAndNumericTextAreCultureIndependent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "DIM a[]=1.5,\"2.5\",EMPTY,2\n"
                + "PRINT CALCARRAY(a,1)\nPRINT CALCARRAY(a,2)\n"
                + "PRINT CALCARRAY(a,3)\nPRINT CALCARRAY(a,4)\n"
                + "PRINT CALCARRAY(a,1,\"1.9\",\"99\")";
            List<string> output = [];
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["6", "1.5", "2.5", "2", "4.5"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, output.Add);
            Assert.Equal(["6", "1.5", "2.5", "2", "4.5"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanAppliesToModesBoundsAndElements()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            DIM a[]=o.Exists("x"),2,EMPTY,3
            PRINT CALCARRAY(a,1)
            PRINT CALCARRAY(a,2)
            PRINT CALCARRAY(a,3)
            PRINT ROUND(CALCARRAY(a,4),-6)
            DIM values[]=1,2,3
            PRINT CALCARRAY(values,1,o.Exists("x"))
            PRINT CALCARRAY(values,1,0,o.Exists("x"))
            TRY
                PRINT CALCARRAY(a,o.Exists("x"))
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["4", "-1", "3", "1.333333", "6", "0", "caught", "1"], output);
    }

    [Fact]
    public async Task NativeArraysAndObjectsKeepTheirInputState()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            DIM a[]=1,"2.5",EMPTY,3
            o.Add("values",a)
            PRINT CALCARRAY(o.Item("values"),1)
            PRINT JOIN(a,"|")
            DIM values[]=o,1
            TRY
                PRINT CALCARRAY(values,1)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT CALCARRAY(values,1,1,1)
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["6.5", "1|2.5||3", "caught", "1", "1"], output);
    }
}
