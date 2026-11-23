using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ReplaceContractTests
{
    [Fact]
    public async Task AliasRoutesNativeBooleanAndStringArgumentsThroughSharedConversion()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("text","abc")
            PRINT REPLACE(o.Exists("text"),"1","x")
            PRINT REPLACE("TRUE-11",o.Exists("text"),"x")
            PRINT REPLACE("a","a",o.Exists("text"))
            PRINT rEpLaCe(o.Item("text"),"b","x")
            PRINT CHGMOJ(o.Item("text"),"b","x")
            TRY
                PRINT REPLACE("","",o)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT o.Count
            """, output.Add);
        Assert.Equal(["-x", "TRUEx1", "-1", "axc", "axc", "caught", "1"], output);
    }
}
