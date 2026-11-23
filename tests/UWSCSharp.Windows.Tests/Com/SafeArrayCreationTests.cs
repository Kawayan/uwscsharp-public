using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 画面や入力を使わず、OleAutの配列生成と値のコピーを検査します。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayCreationTests
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
    public async Task SessionReturnsManagedDefaultsWithNativeTypes(int type)
    {
        await using var session = new ComSession();
        var request = SafeArrayRequest.Read(
            [Value.Numeric(-2), Value.Numeric(-1), Value.Numeric(type)]);
        Value result = await session.CreateSafeArrayAsync(request,
            CancellationToken.None);
        Assert.Equal((ushort)(0x2000 | type), result.NativeVariantType);
        UwscArray array = result.AsArray();
        Assert.Equal((ushort)type, array.NativeElementType);
        Assert.Equal([-2], array.LowerBounds);
        Assert.Equal([-1], array.UpperBounds);
        foreach (Value value in array.Values)
        {
            Assert.Equal((ushort)(type == 12 ? 0 : type), value.NativeVariantType);
            Assert.Equal(type switch
            {
                8 => ValueKind.String,
                9 or 13 => ValueKind.Nothing,
                10 => ValueKind.Error,
                12 => ValueKind.Empty,
                _ => ValueKind.Number
            }, value.Kind);
            if (type == 8)
            {
                Assert.Equal("", value.Text);
                Assert.Null(value.NativeScalar);
            }
        }
    }

    [Fact]
    public async Task SessionRejectsCancelledAndDisposedCreation()
    {
        var session = new ComSession();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var request = SafeArrayRequest.Read([]);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.CreateSafeArrayAsync(request, cancelled.Token));
        }
        finally
        {
            await session.DisposeAsync();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.CreateSafeArrayAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void NativeArrayCopyHonorsItsCancellationContext(int type)
    {
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            ComVariantValue.Write(new ComArrayValue((ushort)type,
                [0], [0], [type == 8 ? "本文" : 1d]), variant);
            var context = new ComVariantValue.ReadContext(cancelled.Token);
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(() =>
                ComSafeArray.Read(Marshal.ReadIntPtr(variant, 8),
                    (ushort)type, 0, context));
            Assert.Equal(cancelled.Token, failure.CancellationToken);
            Assert.Empty(context.Owned);
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }

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
    public void NativeDefaultsKeepElementTagsAndAsymmetricBounds(int type)
    {
        ComVariantValue result = ComSafeArray.Create((ushort)type,
            [-2, 5], [-1, 7]);
        Assert.Equal(0x2000 | type, result.Type);
        ComArrayValue array = Assert.IsType<ComArrayValue>(result.Value);
        Assert.Equal(type, array.ElementType);
        Assert.Equal([-2, 5], array.LowerBounds);
        Assert.Equal([-1, 7], array.UpperBounds);
        Assert.Equal(6, array.Values.Count);
        foreach (object? item in array.Values)
        {
            ComVariantValue element = Assert.IsType<ComVariantValue>(item);
            Assert.Equal(type == 12 ? 0 : type, element.Type);
            switch (type)
            {
                case 8:
                case 9:
                case 12:
                case 13:
                    Assert.Null(element.Value);
                    break;
                case 10:
                    Assert.Equal(0, Assert.IsType<ErrorWrapper>(element.Value).ErrorCode);
                    break;
                case 11:
                    Assert.False(Assert.IsType<bool>(element.Value));
                    break;
                case 7:
                    Assert.Equal(0d, Assert.IsType<double>(element.Value));
                    break;
                default:
                    Assert.Equal(0d, Convert.ToDouble(element.Value));
                    break;
            }
        }
        Assert.Empty(result.Acquisitions!);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IntegerLimitSingletonsKeepTheirBounds(int bound)
    {
        ComArrayValue array = Assert.IsType<ComArrayValue>(
            ComSafeArray.Create(12, [bound], [bound]).Value);
        Assert.Equal([bound], array.LowerBounds);
        Assert.Equal([bound], array.UpperBounds);
        Assert.Single(array.Values);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyAxesKeepAllBounds(bool firstAxisEmpty)
    {
        int[] lower = firstAxisEmpty ? [1, -2] : [-2, 1];
        int[] upper = firstAxisEmpty ? [0, 4] : [4, 0];
        ComArrayValue array = Assert.IsType<ComArrayValue>(
            ComSafeArray.Create(3, lower, upper).Value);
        Assert.Equal(lower, array.LowerBounds);
        Assert.Equal(upper, array.UpperBounds);
        Assert.Empty(array.Values);
    }

    [Fact]
    public void InvalidShapeAndTypeAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ComSafeArray.Create(3, [0], [1_000_000]));
        Assert.Throws<InvalidOperationException>(() =>
            ComSafeArray.Create(3, [0, 0], [999, 1000]));
        Assert.Throws<InvalidOperationException>(() =>
            ComSafeArray.Create(3, [2], [0]));
        Assert.Throws<NotSupportedException>(() =>
            ComSafeArray.Create(15, [0], [0]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("日本語", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("日本語", true)]
    public void BstrRoundtripKeepsNullAndEmptyDistinct(string? text,
        bool byReference)
    {
        nint source = Marshal.AllocCoTaskMem(24);
        nint destination = Marshal.AllocCoTaskMem(24);
        nint reference = Marshal.AllocCoTaskMem(IntPtr.Size);
        nint bstr = text is null ? 0 : Marshal.StringToBSTR(text);
        Marshal.Copy(new byte[24], 0, source, 24);
        Marshal.Copy(new byte[24], 0, destination, 24);
        try
        {
            Marshal.WriteIntPtr(reference, bstr);
            Marshal.WriteInt16(source, (short)(byReference ? 0x4008 : 8));
            Marshal.WriteIntPtr(source, 8, byReference ? reference : bstr);
            var result = ComVariantValue.Read(source);
            Assert.Equal(8, result.Type);
            Assert.Equal(text, result.Value);
            ComVariantValue.Write(result, destination);
            nint pointer = Marshal.ReadIntPtr(destination, 8);
            Assert.Equal(text is null, pointer == 0);
            if (pointer != 0)
            {
                Assert.Equal(text, Marshal.PtrToStringBSTR(pointer));
            }
        }
        finally
        {
            // sourceのBSTRはこのテストが所有し、VARIANTと重複解放しません。
            Marshal.FreeBSTR(bstr);
            Marshal.ThrowExceptionForHR(VariantClear(destination));
            Marshal.FreeCoTaskMem(reference);
            Marshal.FreeCoTaskMem(destination);
            Marshal.FreeCoTaskMem(source);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);

    [Fact]
    public void BstrArraysKeepNullEmptyAndUnicodeDistinct()
    {
        var source = new ComArrayValue(8, [-2], [0],
            [new ComVariantValue(8, null), "", "日本語"]);
        nint variant = Marshal.AllocCoTaskMem(24);
        Marshal.Copy(new byte[24], 0, variant, 24);
        try
        {
            ComVariantValue.Write(source, variant);
            ComArrayValue array = Assert.IsType<ComArrayValue>(
                ComVariantValue.Read(variant).Value);
            Assert.Equal([null, "", "日本語"],
                array.Values.Select(value => (string?)
                    Assert.IsType<ComVariantValue>(value).Value));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.FreeCoTaskMem(variant);
        }
    }
}
