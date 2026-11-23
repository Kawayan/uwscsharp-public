using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ByteTextLanguageCpuIntegrationTests
{
    [Fact]
    public async Task NativeCpuAndLanguageFeaturesRunTogether()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            OPTION SHORTCIRCUIT
            TEXTBLOCK text
            Aあ
            ENDTEXTBLOCK
            PRINT LENGTHB(text)
            PRINT STRCONV("ｶﾞ",SC_FULLWIDTH+SC_HIRAGANA)
            PRINT CPUUSERATE()
            SLEEP(1.1)
            PRINT CPUUSERATE()
            WITH Sample
                IF TRUE OR 1/0 THEN PRINT .value
            ENDWITH
            MODULE Sample
                PUBLIC value=3
            ENDMODULE
            """, output.Add);
        Assert.Equal("3", output[0]);
        Assert.Equal("が", output[1]);
        Assert.Equal("0", output[2]);
        Assert.InRange(double.Parse(output[3], System.Globalization.CultureInfo.InvariantCulture), 0, 100);
        Assert.Equal("3", output[4]);
    }
}
