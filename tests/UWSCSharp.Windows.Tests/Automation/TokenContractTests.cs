using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TokenContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task UnicodeSetsAndFiniteFlagsDoNotDependOnCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
                s="a😁b😀c"
                PRINT TOKEN("😀",s)
                PRINT s
                s=",,a,,b"
                PRINT TOKEN(",",s," 1e20 ")
                PRINT s
                """, output.Add);
            Assert.Equal(["a😁b", "c", "a", "b"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeBooleanUsesCommonFiniteTextForSourceDelimiterAndFlags()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            s=o.Exists("x")
            PRINT "["+TOKEN("1",s)+"]"
            PRINT "["+s+"]"
            s="-1x"
            PRINT "["+TOKEN(o.Exists("x"),s)+"]"
            PRINT s
            s=",,"+CHR(34)+"a,b"+CHR(34)+",,c"
            PRINT TOKEN(",",s,o.Exists("x"),o.Exists("x"))
            PRINT s
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["[-]", "[]", "[]", "1x", "\"a,b\"", "c", "1"], output);
    }

    [Fact]
    public async Task BstrArrayElementRoundTripAndObjectRejectionKeepOtherValues()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            DIM initial[]="😀"+CHR(0)+",rest","other"
            o.Add("values",initial)
            a=o.Item("values")
            PRINT LENGTHS(TOKEN(",",a[0]))
            PRINT JOIN(a,"|")
            PRINT JOIN(initial,"|")
            o.Item("values")=a
            copied=o.Item("values")
            PRINT JOIN(copied,"|")
            s="kept"
            TRY
                TOKEN(o,s)
            EXCEPT
                PRINT s
            ENDTRY
            """, output.Add);
        Assert.Equal(["3", "rest|other", "😀\0,rest|other", "rest|other", "kept"], output);
    }
}
