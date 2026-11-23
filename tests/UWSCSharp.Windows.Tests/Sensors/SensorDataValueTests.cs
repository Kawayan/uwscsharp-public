using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するPROPVARIANTとOSの変換helperだけを使い、COM生成・機器・画面には触れません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorDataValueTests
{
    [Theory]
    [InlineData(VarEnum.VT_BOOL, -1, true)]
    [InlineData(VarEnum.VT_BOOL, 0, false)]
    [InlineData(VarEnum.VT_I4, 1, true)]
    [InlineData(VarEnum.VT_I4, 0, false)]
    [InlineData(VarEnum.VT_EMPTY, 0, false)]
    public void BooleanUsesWindowsConversionWithFalseDefault(
        VarEnum type, int value, bool expected)
    {
        object result = SensorDataValue.Read(SensorDataValueKind.Boolean,
            property => WriteScalar(property, type, value));
        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(VarEnum.VT_LPWSTR, "true", true)]
    [InlineData(VarEnum.VT_LPWSTR, "false", false)]
    [InlineData(VarEnum.VT_LPWSTR, "not a boolean", false)]
    [InlineData(VarEnum.VT_BSTR, "true", true)]
    public void BooleanConvertsOwnedStringsBeforeClearingThem(
        VarEnum type, string value, bool expected)
    {
        object result = SensorDataValue.Read(SensorDataValueKind.Boolean,
            property => WriteString(property, type, value));
        Assert.Equal(expected, Assert.IsType<bool>(result));
    }

    [Theory]
    [InlineData(VarEnum.VT_LPWSTR, "東京都 千代田区", "東京都 千代田区")]
    [InlineData(VarEnum.VT_BSTR, "東京都 千代田区", "東京都 千代田区")]
    [InlineData(VarEnum.VT_LPWSTR, "", "")]
    [InlineData(VarEnum.VT_BSTR, "first\0second", "first")]
    public void StringCopiesUnicodeTextBeforeReleasingProperty(
        VarEnum type, string value, string expected)
    {
        object result = SensorDataValue.Read(SensorDataValueKind.String,
            property => WriteString(property, type, value));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        Assert.Equal(expected, Assert.IsType<string>(result));
    }

    [Theory]
    [InlineData(VarEnum.VT_EMPTY)]
    [InlineData(VarEnum.VT_I4)]
    public void StringWithoutStringStorageUsesOriginalLiteralDefault(VarEnum type)
    {
        object result = SensorDataValue.Read(SensorDataValueKind.String,
            property => WriteScalar(property, type, 123));
        Assert.Equal("<no value>", Assert.IsType<string>(result));
    }

    [Theory]
    [InlineData(VarEnum.VT_UI8, -1L)]
    [InlineData(VarEnum.VT_I8, long.MinValue)]
    [InlineData(VarEnum.VT_EMPTY, 0x123456789abcdef0L)]
    [InlineData(VarEnum.VT_R8, 0x3ff0000000000000L)]
    public void RfidReadsTheSigned64BitUnionWithoutNumericConversion(
        VarEnum type, long bits)
    {
        object result = SensorDataValue.Read(SensorDataValueKind.Integer,
            property => WriteScalar(property, type, bits));
        Assert.Equal(bits, Assert.IsType<long>(result));
    }

    [Fact]
    public void NumericReusesTheTypeSpecificConverter()
    {
        object result = SensorDataValue.Read(SensorDataValueKind.Numeric,
            property => WriteScalar(property, VarEnum.VT_UI8, -1));
        Assert.Equal(18446744073709551616d, Assert.IsType<double>(result));
        result = SensorDataValue.Read(SensorDataValueKind.Numeric, _ => { });
        Assert.Equal(unchecked((long)0xfff8000000000000),
            BitConverter.DoubleToInt64Bits(Assert.IsType<double>(result)));
    }

    [Fact]
    public void EmptyBarcodeDoesNotFetchOrConvertAReport()
    {
        var native = new TrackingNativeApi();
        object result = SensorDataValue.Read(SensorDataValueKind.EmptyString,
            _ => throw new InvalidOperationException(), native);
        Assert.Equal(string.Empty, Assert.IsType<string>(result));
        Assert.Empty(native.Events);
    }

    [Fact]
    public void FailedFetchClearsPartiallyPopulatedPropertyAndPreservesException()
    {
        var native = new TrackingNativeApi();
        var failure = new InvalidOperationException("取得に失敗しました。");
        Exception? actual = Record.Exception(() => SensorDataValue.Read(
            SensorDataValueKind.String, property =>
            {
                Assert.Equal(0, Marshal.ReadInt64(property));
                Assert.Equal(0, Marshal.ReadInt64(property, 8));
                if (IntPtr.Size == 8)
                {
                    Assert.Equal(0, Marshal.ReadInt64(property, 16));
                }

                WriteString(property, VarEnum.VT_LPWSTR, "途中の取得値");
                throw failure;
            }, native));
        Assert.Same(failure, actual);
        Assert.Equal(["clear"], native.Events);
        Assert.Equal("途中の取得値", native.ClearedText);
    }

    [Fact]
    public void ConversionFailureStillClearsThePropertyExactlyOnce()
    {
        var native = new TrackingNativeApi { FailConversion = true };
        Assert.Throws<FormatException>(() => SensorDataValue.Read(
            SensorDataValueKind.String,
            property => WriteString(property, VarEnum.VT_LPWSTR, "取得値"), native));
        Assert.Equal(["string", "clear"], native.Events);
        Assert.Equal("取得値", native.ClearedText);
    }

    [Fact]
    public void SuccessfulCopyPrecedesClear()
    {
        var native = new TrackingNativeApi();
        object result = SensorDataValue.Read(SensorDataValueKind.String,
            property => WriteString(property, VarEnum.VT_BSTR, "所有する値"), native);
        Assert.Equal("所有する値", result);
        Assert.Equal(["string", "clear"], native.Events);
    }

    [Fact]
    public void ConcurrentReadsHaveSeparatePropertyStorage()
    {
        using var barrier = new Barrier(2);
        nint[] addresses = new nint[2];
        object[] values = new object[2];
        Parallel.For(0, 2, index =>
        {
            values[index] = SensorDataValue.Read(SensorDataValueKind.String, property =>
            {
                addresses[index] = property;
                WriteString(property, VarEnum.VT_LPWSTR, $"値{index}");
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            });
        });
        Assert.NotEqual(addresses[0], addresses[1]);
        Assert.Equal("値0", values[0]);
        Assert.Equal("値1", values[1]);
    }

    [Fact]
    public void InvalidKindDoesNotAcquireAProperty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorDataValue.Read(
            (SensorDataValueKind)(-1), _ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentNullException>(() => SensorDataValue.Read(
            SensorDataValueKind.Numeric, null!));
    }

    private static void WriteScalar(nint property, VarEnum type, long bits)
    {
        Marshal.WriteInt16(property, (short)type);
        Marshal.WriteInt64(property, 8, bits);
    }

    private static void WriteString(nint property, VarEnum type, string value)
    {
        nint text = type == VarEnum.VT_BSTR
            ? Marshal.StringToBSTR(value)
            : Marshal.StringToCoTaskMemUni(value);
        Marshal.WriteInt16(property, (short)type);
        Marshal.WriteIntPtr(property, 8, text);
    }

    private sealed class TrackingNativeApi : ISensorPropertyValueNativeApi
    {
        internal List<string> Events { get; } = [];

        internal bool FailConversion
        {
            get; init;
        }

        internal string? ClearedText
        {
            get; private set;
        }

        public bool ReadBoolean(nint propertyValue) =>
            SensorPropertyValueNativeApi.Instance.ReadBoolean(propertyValue);

        public string ReadString(nint propertyValue)
        {
            Events.Add("string");
            if (FailConversion)
            {
                throw new FormatException("変換に失敗しました。");
            }

            return SensorPropertyValueNativeApi.Instance.ReadString(propertyValue);
        }

        public void Clear(nint propertyValue)
        {
            Events.Add("clear");
            ClearedText = Marshal.PtrToStringUni(Marshal.ReadIntPtr(propertyValue, 8));
            SensorPropertyValueNativeApi.Instance.Clear(propertyValue);
            Assert.Equal(0, Marshal.ReadInt16(propertyValue));
        }
    }
}
