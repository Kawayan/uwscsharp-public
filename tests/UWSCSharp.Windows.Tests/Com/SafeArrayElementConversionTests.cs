using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// プリミティブ値だけを使い、COM要素変換を画面なしで検査します。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayElementConversionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void ElementConversionMatchesArrayTransmission(int type)
    {
        object? input = type switch
        {
            7 or 8 => 7d,
            9 or 10 or 13 => null,
            11 => true,
            12 => "本文",
            _ => "7"
        };
        ComVariantValue converted = ComSafeArray.ConvertElement(input, (ushort)type);
        Assert.Equal(type == 12 ? 8 : type, converted.Type);
        switch (type)
        {
            case 8:
                Assert.Equal("7", converted.Value);
                break;
            case 9:
            case 13:
                Assert.Null(converted.Value);
                break;
            case 10:
                Assert.Equal(0, Assert.IsType<ErrorWrapper>(converted.Value).ErrorCode);
                break;
            case 11:
                Assert.True(Assert.IsType<bool>(converted.Value));
                break;
            case 12:
                Assert.Equal("本文", converted.Value);
                break;
            default:
                Assert.Equal(7d, Convert.ToDouble(converted.Value));
                break;
        }

        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(new ComArrayValue((ushort)type,
                [-4], [-4], [input]), variant);
            ComArrayValue array = Assert.IsType<ComArrayValue>(
                ComVariantValue.Read(variant).Value);
            ComVariantValue element = Assert.IsType<ComVariantValue>(Assert.Single(array.Values));
            Assert.Equal(converted.Type, element.Type);
            if (type == 10)
            {
                Assert.Equal(Assert.IsType<ErrorWrapper>(converted.Value).ErrorCode,
                    Assert.IsType<ErrorWrapper>(element.Value).ErrorCode);
            }
            else
            {
                Assert.Equal(converted.Value, element.Value);
            }
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
        Assert.Empty(converted.Acquisitions!);
    }

    [Theory]
    [InlineData(17, 256d)]
    [InlineData(16, -129d)]
    [InlineData(2, 32768d)]
    [InlineData(3, "数値ではない")]
    public void RejectedConversionDoesNotAffectTheNextValue(int type, object input)
    {
        COMException failure = Assert.Throws<COMException>(() =>
            ComSafeArray.ConvertElement(input, (ushort)type));
        Assert.True(ComOperationErrors.Contains(failure));
        ComVariantValue next = ComSafeArray.ConvertElement("7", (ushort)type);
        Assert.Equal(type, next.Type);
        Assert.Equal(7d, Convert.ToDouble(next.Value));
    }

    [Fact]
    public void VariantConversionKeepsNestedArrayBoundsAndNullBstr()
    {
        var input = new ComArrayValue(8, [-2], [-1],
            [new ComVariantValue(8, null), ""]);
        ComVariantValue converted = ComSafeArray.ConvertElement(input, 12);
        Assert.Equal(0x2008, converted.Type);
        ComArrayValue nested = Assert.IsType<ComArrayValue>(converted.Value);
        Assert.Equal([-2], nested.LowerBounds);
        Assert.Equal([-1], nested.UpperBounds);
        Assert.Null(Assert.IsType<ComVariantValue>(nested.Values[0]).Value);
        Assert.Equal("", Assert.IsType<ComVariantValue>(nested.Values[1]).Value);
    }

    [Fact]
    public void DateTextConversionReportsTheSameFailureAsArrayTransmission()
    {
        COMException failure = Assert.Throws<COMException>(() =>
            ComSafeArray.ConvertElement("数値ではない", 7));
        Assert.True(ComOperationErrors.Contains(failure));
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            COMException transmitted = Assert.Throws<COMException>(() =>
                ComVariantValue.Write(new ComArrayValue(7,
                    [0], [0], ["数値ではない"]), variant));
            Assert.Equal(failure.HResult, transmitted.HResult);
            Assert.Equal(0, Marshal.ReadInt16(variant));
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
