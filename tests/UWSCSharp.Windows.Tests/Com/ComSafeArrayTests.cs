using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class ComSafeArrayTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    public void AdditionalTypedArrayElementsRetainPayload(int type)
    {
        object? scalar = type switch
        {
            16 => sbyte.MinValue,
            2 => short.MinValue,
            4 => 1.25f,
            5 => -1.25d,
            11 => true,
            18 => ushort.MaxValue,
            19 => uint.MaxValue,
            20 => long.MaxValue,
            21 => ulong.MaxValue,
            22 => int.MinValue,
            23 => uint.MaxValue,
            8 => "日本語",
            10 => new ErrorWrapper(-42),
            9 or 13 => null,
            _ => throw new InvalidOperationException()
        };
        var source = new ComArrayValue((ushort)type, [int.MinValue], [int.MinValue],
            [new ComVariantValue((ushort)type, scalar)]);
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(source, variant);
            ComArrayValue result = Assert.IsType<ComArrayValue>(ComVariantValue.Read(variant).Value);
            Assert.Equal(type, result.ElementType);
            Assert.Equal([int.MinValue], result.LowerBounds);
            ComVariantValue element = Assert.IsType<ComVariantValue>(Assert.Single(result.Values));
            Assert.Equal(type, element.Type);
            if (type == 10)
            {
                Assert.Equal(-42, Assert.IsType<ErrorWrapper>(element.Value).ErrorCode);
            }
            else
            {
                Assert.Equal(scalar, element.Value);
            }
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeArrayLimitsAreCheckedBeforeValueAllocation(bool excessiveRank)
    {
        Array source = excessiveRank
            ? Array.CreateInstance(typeof(int), Enumerable.Repeat(1, 17).ToArray())
            : new int[1_000_001];
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            Marshal.GetNativeVariantForObject(source, variant);
            Assert.Throws<InvalidOperationException>(() => ComVariantValue.Read(variant));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [Theory]
    [InlineData(3, 42d)]
    [InlineData(17, 255d)]
    [InlineData(14, 12.5d)]
    [InlineData(6, 12.5d)]
    [InlineData(7, -0.25d)]
    public void TypedArraysCoerceElementsAndRetainType(int type, double number)
    {
        object scalar = type == 7 ? new ComVariantValue(7, number) : number;
        var source = new ComArrayValue((ushort)type, [-4], [-4], [scalar]);
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(source, variant);
            ComArrayValue result = Assert.IsType<ComArrayValue>(ComVariantValue.Read(variant).Value);
            Assert.Equal(type, result.ElementType);
            Assert.Equal([-4], result.LowerBounds);
            ComVariantValue element = Assert.IsType<ComVariantValue>(result.Values[0]);
            Assert.Equal(type, element.Type);
            Assert.Equal(number, Convert.ToDouble(element.Value));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [Fact]
    public void VariantArraysRetainEmptyNullErrorAndNestedBounds()
    {
        var nested = new ComArrayValue(8, [5], [5], ["日本語"]);
        var source = new ComArrayValue(12, [-2], [2],
            [null, DBNull.Value, new ErrorWrapper(-42), true, nested]);
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(source, variant);
            ComArrayValue result = Assert.IsType<ComArrayValue>(ComVariantValue.Read(variant).Value);
            Assert.Equal(new ushort[] { 0, 1, 10, 11, 0x2008 },
                result.Values.Select(value => Assert.IsType<ComVariantValue>(value).Type));
            ComArrayValue child = Assert.IsType<ComArrayValue>(
                Assert.IsType<ComVariantValue>(result.Values[4]).Value);
            Assert.Equal([5], child.LowerBounds);
            Assert.Equal("日本語", Assert.IsType<ComVariantValue>(child.Values[0]).Value);
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [Fact]
    public void EmptyArraysKeepBoundsAndInvalidCoercionIsRejected()
    {
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(new ComArrayValue(12, [3, -1], [2, 1], []), variant);
            ComArrayValue result = Assert.IsType<ComArrayValue>(ComVariantValue.Read(variant).Value);
            Assert.Equal([3, -1], result.LowerBounds);
            Assert.Equal([2, 1], result.UpperBounds);
            Assert.Empty(result.Values);
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            COMException failure = Assert.Throws<COMException>(() => ComVariantValue.Write(
                new ComArrayValue(17, [0], [1], [1d, 256d]), variant));
            Assert.True(ComOperationErrors.Contains(failure));
            Assert.Equal(0, Marshal.ReadInt16(variant));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [Fact]
    public void ClrArrayRetainsAsymmetricDimensionsAndBounds()
    {
        var source = Array.CreateInstance(typeof(int), [2, 3], [-2, 5]);
        for (int row = -2; row <= -1; row++)
        {
            for (int column = 5; column <= 7; column++)
            {
                source.SetValue(row * 100 + column, row, column);
            }
        }

        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            Marshal.GetNativeVariantForObject(source, variant);
            ComArrayValue result = Assert.IsType<ComArrayValue>(ComVariantValue.Read(variant).Value);
            Assert.Equal(3, result.ElementType);
            Assert.Equal([-2, 5], result.LowerBounds);
            Assert.Equal([-1, 7], result.UpperBounds);
            Assert.Equal([-195, -194, -193, -95, -94, -93],
                result.Values.Select(value => Assert.IsType<int>(Assert.IsType<ComVariantValue>(value).Value)));
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            ComVariantValue.Write(result, variant);
            Array roundtrip = Assert.IsAssignableFrom<Array>(Marshal.GetObjectForNativeVariant(variant));
            Assert.Equal(-2, roundtrip.GetLowerBound(0));
            Assert.Equal(5, roundtrip.GetLowerBound(1));
            Assert.Equal(-93, roundtrip.GetValue(-1, 7));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);
}
