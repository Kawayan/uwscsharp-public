using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllScalarResultTests
{
    [Theory]
    [InlineData(long.MaxValue, 0x037f, 0x43e0_0000_0000_0000UL)]
    [InlineData(long.MaxValue, 0x077f, 0x43df_ffff_ffff_ffffUL)]
    [InlineData(long.MaxValue, 0x0b7f, 0x43e0_0000_0000_0000UL)]
    [InlineData(long.MaxValue, 0x0f7f, 0x43df_ffff_ffff_ffffUL)]
    [InlineData(long.MinValue, 0x077f, 0xc3e0_0000_0000_0000UL)]
    [InlineData(9007199254740993L, 0x0b7f, 0x4340_0000_0000_0001UL)]
    [InlineData(-9007199254740993L, 0x077f, 0xc340_0000_0000_0001UL)]
    [InlineData(-9007199254740995L, 0x0f7f, 0xc340_0000_0000_0001UL)]
    public void LonglongRoundTripThroughExtendedUsesControlWord(
        long input, ushort controlWord, ulong expected)
    {
        Value value = NativeDllScalarResult.Convert(
            NativeDllPrimitive.Longlong, input, true);
        NativeDllExtendedArgument converted = Assert.IsType<NativeDllExtendedArgument>(
            NativeDllScalarInput.Convert(
                NativeDllPrimitive.Extended, value, controlWord));

        Assert.Equal(expected,
            BitConverter.DoubleToUInt64Bits(converted.Number));
    }

    [Theory]
    [InlineData(6, 1000L, 0x077f, 0x3fb9_9999_9999_9999UL)]
    [InlineData(6, 1000L, 0x0800, 0x3fb9_9999_9999_999aUL)]
    [InlineData(6, -1000L, 0x0b7f, 0xbfb9_9999_9999_9999UL)]
    [InlineData(6, -1000L, 0x0400, 0xbfb9_9999_9999_999aUL)]
    [InlineData(6, -1000L, 0x0f7f, 0xbfb9_9999_9999_9999UL)]
    [InlineData(14, 1000L, 0x077f, 0x3fb9_9999_9999_999aUL)]
    [InlineData(14, -1000L, 0x0b7f, 0xbfb9_9999_9999_999aUL)]
    [InlineData(6, 5629499534213119999L, 0x077f, 0x42ff_ffff_ffff_ffffUL)]
    [InlineData(6, 0L, 0x077f, 0UL)]
    public void ExtendedInputRoundsCurrencyAndPreservesDecimalConversion(
        ushort variantType, long units, ushort controlWord, ulong expected)
    {
        decimal input = units / 10000m;
        Value value = Value.Numeric((double)input) with
        {
            NativeVariantType = variantType,
            NativeScalar = input
        };
        NativeDllExtendedArgument converted = Assert.IsType<NativeDllExtendedArgument>(
            NativeDllScalarInput.Convert(
                NativeDllPrimitive.Extended, value, controlWord));

        Assert.Equal(expected,
            BitConverter.DoubleToUInt64Bits(converted.Number));
    }

    [Theory]
    [InlineData(ulong.MaxValue, 0x037f, 0x43f0_0000_0000_0000UL)]
    [InlineData(ulong.MaxValue, 0x077f, 0x43ef_ffff_ffff_ffffUL)]
    [InlineData(ulong.MaxValue, 0x0f7f, 0x43ef_ffff_ffff_ffffUL)]
    [InlineData(9007199254740993UL, 0x0b7f, 0x4340_0000_0000_0001UL)]
    public void UnsignedExtendedInputUsesControlWord(
        ulong input, ushort controlWord, ulong expected)
    {
        Value value = Value.Numeric(input) with
        {
            NativeVariantType = 21,
            NativeScalar = input
        };
        NativeDllExtendedArgument converted = Assert.IsType<NativeDllExtendedArgument>(
            NativeDllScalarInput.Convert(
                NativeDllPrimitive.Extended, value, controlWord));

        Assert.Equal(expected,
            BitConverter.DoubleToUInt64Bits(converted.Number));
    }

    public static TheoryData<string, object, double, int> Results => new()
    {
        { "Int", int.MinValue, int.MinValue, 3 },
        { "Long", int.MaxValue, int.MaxValue, 3 },
        { "Bool", -1, -1, 3 },
        { "Byte", byte.MaxValue, 255, 17 },
        { "Boolean", (byte)42, 42, 17 },
        { "Word", ushort.MaxValue, 65535, 18 },
        { "Uint", uint.MaxValue, uint.MaxValue, 5 },
        { "Dword", 0x80000000U, 2147483648d, 5 },
        { "Float", 1.5f, 1.5d, 5 },
        { "Double", -2.25d, -2.25d, 5 },
        { "Extended", 1.5d, 1.5d, 5 },
        { "Longlong", long.MinValue, long.MinValue, 20 }
    };

    [Theory]
    [MemberData(nameof(Results))]
    public void NumericValueAndVariantTypeFollowReturnPath(string name, object input, double number, int tag)
    {
        Value value = NativeDllScalarResult.Convert(Enum.Parse<NativeDllPrimitive>(name), input, true);
        Assert.Equal(number, value.Number);
        Assert.Equal(tag, VariantTypeRequest.Query(value).Number);
        Assert.False(value.IsLogical);
        Assert.Null(value.NativeReferenceType);
    }

    [Theory]
    [InlineData("Byte")]
    [InlineData("Boolean")]
    [InlineData("Word")]
    public void IntegerWidthFlagCanSelectInt32(string name)
    {
        object input = name == "Word" ? (object)(ushort)42 : (byte)42;
        Value value = NativeDllScalarResult.Convert(Enum.Parse<NativeDllPrimitive>(name), input, false);
        Assert.Equal(3, VariantTypeRequest.Query(value).Number);
        Assert.Equal(42, Assert.IsType<int>(value.GetUnchangedNativeScalar()));
    }

    [Fact]
    public void LonglongPreservesBitsBeyondDoublePrecisionUntilValueChanges()
    {
        const long original = 9007199254740993;
        Value value = NativeDllScalarResult.Convert(NativeDllPrimitive.Longlong, original, false);
        Assert.Equal(original, Assert.IsType<long>(value.GetUnchangedNativeScalar()));
        Assert.Equal(20, VariantTypeRequest.Query(value).Number);
        Value changed = value with
        {
            Number = 1
        };
        Assert.Null(changed.GetUnchangedNativeScalar());
        Assert.Equal(5, VariantTypeRequest.Query(changed).Number);
    }

    [Theory]
    [InlineData(true, 17)]
    [InlineData(false, 3)]
    public void VoidKeepsInitializedZero(bool preserveWidth, int tag)
    {
        Value value = NativeDllScalarResult.Convert(NativeDllPrimitive.Void, null, preserveWidth);
        Assert.Equal(0, value.Number);
        Assert.Equal(tag, VariantTypeRequest.Query(value).Number);
    }

    [Fact]
    public void FloatingResultsKeepNegativeZeroAndRejectNonFiniteValues()
    {
        Value single = NativeDllScalarResult.Convert(NativeDllPrimitive.Float, -0f, true);
        Value number = NativeDllScalarResult.Convert(NativeDllPrimitive.Double, -0d, true);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(single.Number));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(number.Number));
        Assert.Throws<InvalidOperationException>(() =>
            NativeDllScalarResult.Convert(NativeDllPrimitive.Float, float.NaN, true));
        Assert.Throws<InvalidOperationException>(() =>
            NativeDllScalarResult.Convert(NativeDllPrimitive.Double, double.PositiveInfinity, true));
    }

    [Fact]
    public void CharacterResultsPreserveSingleNativeUnitsIncludingNul()
    {
        Value ansi = NativeDllScalarResult.Convert(
            NativeDllPrimitive.Char, (byte)147, true);
        Value wide = NativeDllScalarResult.Convert(
            NativeDllPrimitive.Wchar, (ushort)0xd83d, true);
        Assert.Equal(256, VariantTypeRequest.Query(ansi).Number);
        Assert.Equal(258, VariantTypeRequest.Query(wide).Number);
        Assert.Equal((byte)147, Assert.IsType<byte>(NativeDllScalarInput.Convert(
            NativeDllPrimitive.Char, ansi, 0)));
        Assert.Equal((ushort)0xd83d, Assert.IsType<ushort>(NativeDllScalarInput.Convert(
            NativeDllPrimitive.Wchar, wide, 0)));

        Value ansiNul = NativeDllScalarResult.Convert(
            NativeDllPrimitive.Char, (byte)0, true);
        Value wideNul = NativeDllScalarResult.Convert(
            NativeDllPrimitive.Wchar, (ushort)0, true);
        Assert.Equal("\0", ansiNul.Text);
        Assert.Equal("\0", wideNul.Text);
    }

    [Theory]
    [InlineData("HWND")]
    [InlineData("HMENU")]
    public void HandleResultsRetainNativeBitsAndDoubleVariant(string type)
    {
        NativeDllPrimitive primitive = Enum.Parse<NativeDllPrimitive>(type, ignoreCase: true);
        Assert.Equal(typeof(nuint), NativeDllScalarInput.AbiType(primitive));
        Assert.Equal(nuint.MaxValue, NativeDllScalarInput.Convert(
            primitive, Value.Numeric(-1), 0));
        Assert.Equal((nuint)4, NativeDllScalarInput.Convert(
            primitive, Value.Numeric(3.5), 0));
        Assert.Equal((nuint)3, NativeDllScalarInput.Convert(
            primitive, Value.Numeric(3.5), 0x0400));
        Assert.Equal((nuint)0, NativeDllScalarInput.Convert(
            primitive, new Value(ValueKind.Null), 0));

        Value returned = NativeDllScalarResult.Convert(
            primitive, nuint.MaxValue, true);
        Assert.Equal(5, VariantTypeRequest.Query(returned).Number);
        Assert.Equal((ulong)nuint.MaxValue, returned.GetUnchangedNativeScalar());
        Assert.Equal(nuint.MaxValue, NativeDllScalarInput.Convert(
            primitive, returned, 0));
    }

    [Fact]
    public void WrongAbiTypesAndUnimplementedReturnsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Bool, true, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Byte, 1, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Long, 1L, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Void, 0, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Int, null, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Extended, 1f, true));
        Assert.Throws<NotSupportedException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Variant, 1d, true));
        Assert.Throws<ArgumentException>(() => NativeDllScalarResult.Convert(NativeDllPrimitive.Hwnd, (nint)1, true));
    }
}
