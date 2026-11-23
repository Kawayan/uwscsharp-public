using System.Buffers.Binary;
using System.Collections;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class FileDropPayloadTests
{
    [Fact]
    public void PreservesPointUnicodeOrderAndDoubleTerminator()
    {
        string[] paths = ["C:\\日本語😀\\first.txt", "\\\\server\\share\\second.txt"];
        byte[] bytes = FileDropPayload.Create(paths, int.MinValue, int.MaxValue);
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)));
        char[] units = [.. bytes.AsSpan(20).ToArray().Chunk(2)
            .Select(pair => (char)(pair[0] | (pair[1] << 8)))];
        Assert.Equal(paths[0] + '\0' + paths[1] + "\0\0", new string(units));
    }

    [Fact]
    public void PreservesAllPathsInLargePayload()
    {
        const int count = 10_000;
        string[] paths = [.. Enumerable.Range(0, count)
            .Select(index => $"C:\\日本語😀\\file-{index}.txt")];
        byte[] bytes = FileDropPayload.Create(paths, 17, 29);
        string names = System.Text.Encoding.Unicode.GetString(bytes, 20,
            bytes.Length - 20);

        Assert.Equal(string.Join('\0', paths) + "\0\0", names);
    }

    [Fact]
    public void PreservesUnpairedUtf16CodeUnit()
    {
        string path = "C:\\name-\ud800.txt";
        byte[] bytes = FileDropPayload.Create([path], 0, 0);
        char[] units = [.. bytes.AsSpan(20).ToArray().Chunk(2)
            .Select(pair => (char)(pair[0] | (pair[1] << 8)))];
        Assert.Equal(path + "\0\0", new string(units));
    }

    [Fact]
    public void RejectsMissingPaths()
    {
        Assert.Throws<ArgumentNullException>(() => FileDropPayload.Create(null!, 0, 0));
        Assert.Throws<ArgumentException>(() => FileDropPayload.Create([], 0, 0));
        Assert.Throws<ArgumentException>(() => FileDropPayload.Create([null!], 0, 0));
    }

    [Fact]
    public void StopsReadingPathsWhenCanceledDuringPayloadPreparation()
    {
        using var cancellation = new CancellationTokenSource();
        var paths = new CancelingPaths(cancellation);

        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            FileDropPayload.Create(paths, 0, 0, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, paths.Visited);
    }

    private sealed class CancelingPaths(CancellationTokenSource cancellation)
        : IReadOnlyList<string>
    {
        public int Count => 10_000;

        public int Visited
        {
            get; private set;
        }

        public string this[int index] => $"C:\\file-{index}.txt";

        public IEnumerator<string> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                Visited++;
                if (index == 1)
                {
                    cancellation.Cancel();
                }

                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\before\0after")]
    [InlineData("relative.txt")]
    [InlineData("C:drive-relative.txt")]
    [InlineData("\\root-relative.txt")]
    public void RejectsInvalidOrUnresolvedPath(string path)
        => Assert.Throws<ArgumentException>(() => FileDropPayload.Create([path], 0, 0));
}
