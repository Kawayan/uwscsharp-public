using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Fact]
    public async Task WithAndEncodedUi1ArraysCrossTheOwnedComBoundary()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        fixture.Operation = (arguments, count, result) =>
        {
            Assert.Equal(1u, count);
            Assert.Equal(0x2011, Marshal.ReadInt16(arguments));
            byte[] received = Assert.IsType<byte[]>(Marshal.GetObjectForNativeVariant(arguments));
            Assert.Equal(call++ == 1 ? [65, 0, 66, 48] : [65, 130, 160], received);
            ComVariantValue.Write(new ComArrayValue(17, [0], [2], [65d, 130d, 160d]), result);
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
                PUBLIC object=EMPTY, calls=0
                object=CREATEOLEOBJ("fixture")
                WITH PickObject()
                    .Change(ENCODE("Aあ",CODE_BYTEARRAY))
                    .Change(ENCODE("Aあ",CODE_BYTEARRAYW))
                    DIM a=.Change(ENCODE("Aあ",CODE_BYTEARRAY))
                    PRINT DECODE(a,CODE_BYTEARRAY)
                ENDWITH
                PRINT calls
                FUNCTION PickObject()
                    calls=calls+1
                    RESULT=object
                FEND
                """, output.Add);
        Assert.Equal(["Aあ", "1"], output);
        Assert.Equal(3, call);
        Assert.Null(fixture.Failure);
    }
}
