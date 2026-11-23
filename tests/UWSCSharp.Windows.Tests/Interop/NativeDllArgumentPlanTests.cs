using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllArgumentPlanTests
{
    [Theory]
    [InlineData(0x8000_0000_0000_0000UL, "000000000000000000800000")]
    [InlineData(0x0000_0000_0000_0001UL, "0000000000000080CD3B0000")]
    [InlineData(0x3ff8_0000_0000_0000UL, "00000000000000C0FF3F0000")]
    [InlineData(0x7fef_ffff_ffff_ffffUL, "00F8FFFFFFFFFFFFFE430000")]
    public void ExtendedSlotEncodesFiniteBinary64WithoutRounding(ulong bits, string expected)
    {
        Span<byte> slot = new byte[NativeDllX86Control.ExtendedSlotSize];
        NativeDllExtendedCodec.Write(slot, BitConverter.UInt64BitsToDouble(bits));
        Assert.Equal(expected, Convert.ToHexString(slot));
    }

    [Theory]
    [InlineData("00000000000000000080", 0x8000_0000_0000_0000UL)]
    [InlineData("0000000000000080CC3B", 0UL)]
    [InlineData("0100000000000080CC3B", 1UL)]
    [InlineData("00000000000000B00040", 0x4006_0000_0000_0000UL)]
    public void ExtendedBufferRoundsOnceToBinary64(string bytes, ulong expected)
    {
        double decoded = NativeDllExtendedCodec.Decode(Convert.FromHexString(bytes));
        Assert.Equal(expected, BitConverter.DoubleToUInt64Bits(decoded));
    }

    [Theory]
    [InlineData("0004000000000080FF3F", 0x0800, 0x3ff0_0000_0000_0001UL)]
    [InlineData("0004000000000080FFBF", 0x0400, 0xbff0_0000_0000_0001UL)]
    [InlineData("000C000000000080FF3F", 0x0c00, 0x3ff0_0000_0000_0001UL)]
    [InlineData("0000000000000080CB3B", 0x0800, 1UL)]
    [InlineData("01000000000000000080", 0x0400, 0x8000_0000_0000_0001UL)]
    [InlineData("0000000000000080FF43", 0x0c00, 0x7fef_ffff_ffff_ffffUL)]
    [InlineData("0000000000000080FF7F", 0x0c00, 0x7ff0_0000_0000_0000UL)]
    public void ExtendedBufferAppliesRoundingDirection(string bytes,
        ushort controlWord, ulong expected)
    {
        double decoded = NativeDllExtendedCodec.Decode(
            Convert.FromHexString(bytes), controlWord);
        Assert.Equal(expected, BitConverter.DoubleToUInt64Bits(decoded));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void ExtendedArrayRejectsAnIncompleteElement(int length)
    {
        byte[] bytes = new byte[length];

        Assert.Throws<ArgumentException>("bytes", () =>
            NativeDllExtendedCodec.DecodeArray(bytes, 0));
    }

    [Fact]
    public void ExtendedArgumentsCountAsTwelveBytesTowardTheBlockLimit()
    {
        string values = string.Join(",", Enumerable.Repeat("extended", 170));
        NativeDllDeclaration fits = NativeDllDeclarationParser.Parse(
            $"Read({values},int,int):fixture.dll", 1);
        NativeDllDeclaration tooLarge = NativeDllDeclarationParser.Parse(
            $"Read({values},int,int,int):fixture.dll", 1);
        NativeDllArgumentPlan.CheckArgumentBlockSize(fits.Parameters);
        Assert.Throws<InvalidOperationException>(() =>
            NativeDllArgumentPlan.CheckArgumentBlockSize(tooLarge.Parameters));
    }

    [Fact]
    public void DeclarationBuildsX86ArgumentsAndTextWriteback()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "WriteFields({byte, VAR WSTRING, byte}, VAR int[], longlong):wstring:fixture.dll",
            1);
        const long exact = 9007199254740993;
        object?[][] fields =
        [
            [(byte)11, Value.String("ABC"), (byte)22],
            [10, 20]
        ];
        NativeDllRelayCall call = NativeDllArgumentPlan.PrepareRelay(
            declaration, [fields[0], fields[1], exact],
            CallingConvention.StdCall, default).Call;

        Assert.Equal("WriteFields", call.Export);
        Assert.Equal(NativeDllRelayType.Utf16Text, call.ReturnType);
        Assert.Equal(new(NativeDllRelayType.BufferPointer, Buffer: 1), call.Arguments[0]);
        Assert.Equal(new(NativeDllRelayType.BufferPointer, Buffer: 2), call.Arguments[1]);
        Assert.Equal(NativeDllRelayType.Int64, call.Arguments[2].Type);
        Assert.Equal(exact, NativeDllScalarCodec.Decode(typeof(long), call.Arguments[2].Value));
        Assert.Equal("4100420043000000", Convert.ToHexString(call.Buffers[0]));
        Assert.Equal("0B0000000000000016000000", Convert.ToHexString(call.Buffers[1]));
        Assert.Equal("0A00000014000000", Convert.ToHexString(call.Buffers[2]));
        Assert.Equal(new NativeDllBufferLink(1, 4, 0), Assert.Single(call.Links));
        Assert.Equal(
            [new(0, 0, 8), new(1, 0, 4), new(1, 8, 4), new(2, 0, 8),
                new(1, 4, 0, NativeDllRelayOutputKind.Utf16Text)],
            call.Outputs);
    }

    [Fact]
    public void RelayAppliesRoundingToEachExtendedBufferShape()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "Write(VAR extended,extended[],{extended,VAR extended,extended[]}):int:fixture.dll",
            1);
        var value = new NativeDllExtendedArgument(1);
        object?[] fields = [value, value, new object?[] { value }];
        object?[] arguments = [value, new object?[] { value }, fields];
        const ushort controlWord = 0x037f;
        const ushort returnedControlWord = 0x0b7f;
        (NativeDllRelayCall call, Func<NativeDllRelayResult, object?> apply) =
            NativeDllArgumentPlan.PrepareRelay(declaration, arguments,
                CallingConvention.StdCall, default, controlWord);
        Assert.Equal(controlWord, call.ControlWord);
        Assert.True(call.CaptureControlWord);

        byte[] midpoint = Convert.FromHexString("0004000000000080FF3F");
        byte[][] outputs = [.. call.Outputs.Select(output =>
        {
            byte[] bytes = new byte[output.Length];
            if (bytes.Length >= midpoint.Length)
            {
                midpoint.CopyTo(bytes, 0);
            }

            return bytes;
        })];
        Assert.Equal(7, apply(new NativeDllRelayResult(
            NativeDllScalarCodec.Encode(7), outputs, returnedControlWord)));

        double expected = Math.BitIncrement(1);
        Assert.Equal(expected, arguments[0]);
        Assert.Equal(expected, Assert.Single(Assert.IsType<object[]>(arguments[1])));
        Assert.Equal(expected, fields[0]);
        Assert.Equal(expected, fields[1]);
        Assert.Equal(expected, Assert.Single(Assert.IsType<object[]>(fields[2])));
    }

    [Fact]
    public void RelayWritebackChecksResponseBeforeUpdatingArguments()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "WritePair(VAR int, VAR int):int:fixture.dll", 1);
        object?[] arguments = [2, 3];
        (NativeDllRelayCall Call, Func<NativeDllRelayResult, object?> Apply) prepared = NativeDllArgumentPlan.PrepareRelay(
            declaration, arguments, CallingConvention.StdCall, default);
        byte[] first = NativeDllScalarCodec.Encode(12);
        byte[] returned = NativeDllScalarCodec.Encode(3);
        Assert.Throws<InvalidOperationException>(() =>
            prepared.Apply(new NativeDllRelayResult(returned, [first, [0]])));
        Assert.Equal([2, 3], arguments);

        Assert.Equal(3, prepared.Apply(new NativeDllRelayResult(returned,
            [first, NativeDllScalarCodec.Encode(23)])));
        Assert.Equal([12, 23], arguments);
    }

    [Fact]
    public void RelayHandleWritebackPreservesTextWhenChildRetainsItsAddress()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "Inspect({byte,HWND,word}):int:fixture.dll", 1);
        var text = Value.String("AB");
        object?[] fields = [(byte)3, text, (ushort)5];
        (NativeDllRelayCall call, Func<NativeDllRelayResult, object?> apply) =
            NativeDllArgumentPlan.PrepareRelay(declaration, [fields],
                CallingConvention.StdCall, default);
        Assert.Equal(new NativeDllRelayOutput(1, 4, 0, NativeDllRelayOutputKind.Handle),
            call.Outputs[^1]);

        byte[][] outputs = [.. call.Outputs.Select(output => output.Kind == NativeDllRelayOutputKind.Handle
            ? []
            : call.Buffers[output.Buffer].AsSpan(output.Offset, output.Length).ToArray())];
        Assert.Equal(1, apply(new NativeDllRelayResult(NativeDllScalarCodec.Encode(1), outputs)));
        Assert.Equal(text, fields[1]);

        outputs[^1] = NativeDllScalarCodec.Encode(123u);
        Assert.Equal(1, apply(new NativeDllRelayResult(NativeDllScalarCodec.Encode(1), outputs)));
        Assert.Equal((nuint)123, fields[1]);
    }
}
