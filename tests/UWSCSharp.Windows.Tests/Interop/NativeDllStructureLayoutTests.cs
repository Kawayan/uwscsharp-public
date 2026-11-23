using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllStructureLayoutTests
{
    [Theory]
    [InlineData(false, 24, 8, 16)]
    [InlineData(true, 13, 1, 9)]
    public void LayoutMatchesNativeFixture(bool packed, int size, int second, int third)
    {
        var layout = new NativeDllStructureLayout(
            [typeof(byte), typeof(double), typeof(uint)], packed);
        Assert.Equal(size, layout.Size);
        Assert.Equal([0, second, third], layout.Offsets);
        object?[] values = [(byte)7, 12.5d, uint.MaxValue];
        byte[] bytes = layout.Encode(values);
        Assert.Equal("0000000000002940", Convert.ToHexString(bytes.AsSpan(second, 8)));
        Assert.Equal("FFFFFFFF", Convert.ToHexString(bytes.AsSpan(third, 4)));
        Assert.Equal(values, layout.Decode(bytes));
        if (!packed)
        {
            Assert.All(bytes.AsSpan(1, 7).ToArray(), value => Assert.Equal(0, value));
            Assert.All(bytes.AsSpan(20, 4).ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Fact]
    public void PointerIsAFieldAndInputTypesAreCopied()
    {
        Type[] types = [typeof(byte), typeof(nint), typeof(ushort)];
        var layout = new NativeDllStructureLayout(types, false);
        types[1] = typeof(byte);
        Assert.Equal(nint.Size, layout.Offsets[1]);
        Assert.Equal(nint.Size * 3, layout.Size);
        object?[] values = [(byte)1, (nint)(-1), ushort.MaxValue];
        Assert.Equal(values, layout.Decode(layout.Encode(values)));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)layout.Offsets)[0] = 5);
    }

    [Fact]
    public void PackedStructureCanAlignOnlyItsChildPointer()
    {
        var layout = new NativeDllStructureLayout(
            [typeof(byte), typeof(nint), typeof(byte), typeof(ushort)],
            packed: true, alignedPackedPointerFields: new HashSet<int> { 1 });

        Assert.Equal([0, nint.Size, nint.Size * 2, nint.Size * 2 + 1],
            layout.Offsets);
        Assert.Equal(nint.Size * 3, layout.Size);
    }

    [Fact]
    public void PackedTextPointerIsUnalignedBesideAlignedChildPointer()
    {
        var layout = new NativeDllStructureLayout(
            [typeof(byte), typeof(nint), typeof(byte), typeof(nint)],
            packed: true, alignedPackedPointerFields: new HashSet<int> { 3 });

        Assert.Equal([0, 1, nint.Size + 1, nint.Size * 2], layout.Offsets);
        Assert.Equal(nint.Size * 3, layout.Size);
    }

    [Fact]
    public void UsesX86PointerWidthForNormalAndPackedLayouts()
    {
        Type[] types = [typeof(byte), typeof(nint), typeof(byte), typeof(nuint)];
        var regular = new NativeDllStructureLayout(types, packed: false, pointerSize: 4);
        var packed = new NativeDllStructureLayout(types, packed: true, pointerSize: 4,
            alignedPackedPointerFields: new HashSet<int> { 1 });
        object?[] fields = [(byte)1, (nint)(-2), (byte)3, (nuint)uint.MaxValue];

        Assert.Equal([0, 4, 8, 12], regular.Offsets);
        Assert.Equal([0, 4, 8, 9], packed.Offsets);
        Assert.Equal(16, regular.Size);
        Assert.Equal(16, packed.Size);
        Assert.Equal("01000000FEFFFFFF03000000FFFFFFFF",
            Convert.ToHexString(regular.Encode(fields)));
        Assert.Equal(fields, regular.Decode(regular.Encode(fields)));
        Assert.Equal(fields, packed.Decode(packed.Encode(fields)));

        object?[] handles = [(nuint)uint.MaxValue, (nuint)1];
        byte[] array = NativeDllScalarCodec.EncodeArray(typeof(nuint), handles, pointerSize: 4);
        Assert.Equal("FFFFFFFF01000000", Convert.ToHexString(array));
        Assert.Equal(handles, NativeDllScalarCodec.DecodeArray(typeof(nuint), array, pointerSize: 4));
        if (nuint.Size == 8)
        {
            nuint tooLarge = unchecked((nuint)0x100000000UL);
            Assert.Throws<OverflowException>(() => NativeDllScalarCodec.Encode(tooLarge, pointerSize: 4));
        }
    }

    [Fact]
    public void SmallerFieldsAlignIndependently()
    {
        var layout = new NativeDllStructureLayout(
            [typeof(byte), typeof(short), typeof(float), typeof(byte)], false);
        Assert.Equal([0, 2, 4, 8], layout.Offsets);
        Assert.Equal(12, layout.Size);
    }

    [Fact]
    public void InlineTextLengthDoesNotDetermineAlignment()
    {
        var layout = new NativeDllStructureLayout(
            [typeof(byte), typeof(byte[]), typeof(byte)],
            packed: false, inlineFieldSizes: [0, 3, 0]);
        byte[] text = [0x61, 0x62, 0x63];
        byte[] encoded = layout.Encode([(byte)7, text, (byte)42]);
        Assert.Equal([0, 4, 7], layout.Offsets);
        Assert.Equal("070000006162632A", Convert.ToHexString(encoded));
        Assert.Equal(text, Assert.IsType<byte[]>(layout.Decode(encoded)[1]));
    }

    [Fact]
    public void InvalidShapesAndUnconvertedValuesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new NativeDllStructureLayout(null!, false));
        Assert.Throws<ArgumentException>(() => new NativeDllStructureLayout([], false));
        Assert.Throws<ArgumentException>(() => new NativeDllStructureLayout([typeof(bool)], false));
        var layout = new NativeDllStructureLayout([typeof(int)], false);
        Assert.Throws<ArgumentNullException>(() => layout.Encode(null!));
        Assert.Throws<ArgumentException>(() => layout.Encode([]));
        Assert.Throws<ArgumentException>(() => layout.Encode([null]));
        Assert.Throws<ArgumentException>(() => layout.Encode([1d]));
        Assert.Throws<ArgumentException>(() => layout.Decode(new byte[3]));
        Assert.Throws<ArgumentException>(() => layout.Decode(new byte[5]));
    }
}
