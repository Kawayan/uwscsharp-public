using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class ComReferenceMarshallingTests
{
    [Theory]
    [InlineData(2, "-123")]
    [InlineData(3, "-123456")]
    [InlineData(4, "1.25")]
    [InlineData(5, "1.25")]
    [InlineData(6, "12.5")]
    [InlineData(7, "2023-03-15T06:00:00")]
    [InlineData(8, "日本語")]
    [InlineData(11, "True")]
    [InlineData(13, "NOTHING")]
    [InlineData(14, "12.5")]
    [InlineData(16, "-12")]
    [InlineData(17, "250")]
    [InlineData(18, "65000")]
    [InlineData(19, "4000000000")]
    [InlineData(20, "9223372036854775807")]
    [InlineData(21, "18446744073709551615")]
    [InlineData(22, "-123456")]
    [InlineData(23, "4000000000")]
    public async Task NativeScalarPrintUsesAdoptedValueRepresentation(int type, string expected)
    {
        object? value = type switch
        {
            2 => (short)-123,
            3 or 22 => -123456,
            4 => 1.25f,
            5 => 1.25d,
            6 or 14 => 12.5m,
            7 => 45000.25d,
            8 => "日本語",
            11 => true,
            13 => null,
            16 => (sbyte)-12,
            17 => (byte)250,
            18 => (ushort)65000,
            19 or 23 => 4000000000u,
            20 => long.MaxValue,
            21 => ulong.MaxValue,
            _ => throw new InvalidOperationException()
        };
        using var fixture = new DispatchFixture();
        fixture.Operation = (_, _, result) =>
            ComVariantValue.Write(new ComVariantValue((ushort)type, value), result);
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            PRINT o.Change()
            """, output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal([expected], output);
    }

    [Fact]
    public async Task PrintingNativeObjectDoesNotInvokeDefaultMemberOrKeepAcquisitions()
    {
        using var fixture = new DispatchFixture();
        bool invoked = false;
        fixture.Operation = (_, _, _) => invoked = true;
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM alias=o
            PRINT o
            PRINT alias
            PRINT o=alias
            """, output.Add);
        Assert.Equal(["OBJECT(1)", "OBJECT(1)", "1"], output);
        Assert.False(invoked);
        Assert.Null(fixture.Failure);
    }
}
