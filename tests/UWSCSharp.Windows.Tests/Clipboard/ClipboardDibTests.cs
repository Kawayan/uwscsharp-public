using System.Buffers.Binary;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ClipboardDibTests
{
    public static IEnumerable<object[]> Formats()
    {
        using Stream stream = typeof(ClipboardDibTests).Assembly
            .GetManifestResourceStream(
                "UWSCSharp.Windows.Tests.Clipboard.peekcolor-cases.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() != "bytes"
                || row.GetProperty("format").GetInt32() is not (8 or 17))
            {
                continue;
            }

            yield return [row.GetProperty("name").GetString()!,
                Convert.FromBase64String(row.GetProperty("data").GetString()!),
                row.GetProperty("expected").EnumerateArray()
                    .Select(value => value.GetInt32()).ToArray()];
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void StandardFormatsPreserveColorAndRejectEveryTruncation(
        string name, byte[] data, int[] expected)
    {
        Assert.NotEmpty(name);
        PixelImage image = ClipboardDib.Decode(data, default);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(expected, image.Pixels);
        for (int length = 0; length < data.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() =>
                ClipboardDib.Decode(data[..length], default));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ClipboardDib.Decode(data, cancellation.Token));
    }

    [Theory]
    [InlineData(4, -1)]
    [InlineData(4, int.MaxValue)]
    [InlineData(8, int.MinValue)]
    [InlineData(8, 0)]
    [InlineData(32, int.MaxValue)]
    [InlineData(40, 0)]
    [InlineData(40, 0xff00)]
    [InlineData(40, 0x550000)]
    public void InvalidDimensionsPaletteAndMasksAreRejected(int offset,
        int value)
    {
        object[] row = Formats().Single(row => (string)row[0] == "dib-32-bitfields");
        byte[] data = (byte[])row[1];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() =>
            ClipboardDib.Decode(data, default));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void RleLiteralAndDeltaPreserveRows(int bits)
    {
        object[] row = Formats().Single(row =>
            (string)row[0] == $"dib-rle{bits}");
        byte[] header = ((byte[])row[1])[..56];
        int[] colors = (int[])row[2];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
        byte[] literal = bits == 8
            ? [0, 4, 0, 1, 2, 3, 0, 1] : [0, 4, 0x01, 0x23, 0, 1];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), literal.Length);
        Assert.Equal(colors,
            ClipboardDib.Decode([.. header, .. literal], default).Pixels);

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 2);
        byte repeated = bits == 8 ? (byte)1 : (byte)0x11;
        byte[] delta = [0, 2, 1, 0, 1, 3, 0, 0, 2, repeated, 0, 1];
        if (bits == 4)
        {
            delta[5] = 0x33;
        }

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), delta.Length);
        Assert.Equal([colors[1], colors[1], colors[0], colors[3]],
            ClipboardDib.Decode([.. header, .. delta], default).Pixels);
        foreach (byte[] invalid in new byte[][]
        {
            [0, 2, 255, 0, 0, 1],
            [0, 2, 0, 255, 0, 1],
            [3, repeated, 0, 1],
            [0, 4, 0],
            [1, 255, 0, 1]
        })
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), invalid.Length);
            Assert.Throws<InvalidDataException>(() =>
                ClipboardDib.Decode([.. header, .. invalid], default));
        }
    }
}
