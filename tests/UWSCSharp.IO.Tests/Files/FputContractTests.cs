using System.Globalization;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Testing;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class FputContractTests : IDisposable
{
    private readonly TemporaryTestDirectory temporary =
        new("UWSCSharp-fput-");

    private string directory => temporary.Path;

    public void Dispose() => temporary.Dispose();

    private static Value N(double value) => Value.Numeric(value);

    private static Value S(string value) => Value.String(value);

    private string Fixture(string text)
    {
        string path = Path.Combine(directory, Guid.NewGuid() + ".txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task PreparedRowChangesSaveOnlyOnClose()
    {
        string path = Fixture("one\r\ntwo\r\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(18)], default);
        Assert.Equal(Value.Empty, await session.InvokeAsync("FPUT",
            [id, S("append")], default));
        await session.InvokeAsync("FPUT", [id, S("first"), N(1)], default);
        await session.InvokeAsync("FPUT", [id, S("insert"), N(2), N(-1)], default);
        await session.InvokeAsync("FPUT", [id, S("last"), N(6)], default);
        Assert.Equal(S("first\r\ninsert\r\ntwo\r\nappend\r\n\r\nlast\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FPUT", [id, S("new\ntext\rfin"), N(-2)], default);
        Assert.Equal(N(3), await session.InvokeAsync("FGET", [id, N(-1)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("new\r\ntext\r\nfin\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("a,\"b,c\",d", 2, 0, "X", "a,X,d")]
    [InlineData("a,\"b,c\",d", 2, 3, "X", "a,X,c\",d")]
    [InlineData("a,b", 5, 0, "X", "a,b,,,X")]
    [InlineData("", 3, 0, "X", ",,X")]
    [InlineData("a,b,", 2, 0, "X", "a,X")]
    [InlineData("a,b,,", 2, 0, "X", "a,X,,")]
    [InlineData("a,b, \0 ", 2, 0, "X", "a,X")]
    [InlineData("a,b,\t ", 2, 0, "X", "a,X,\t ")]
    [InlineData("a,b,<_#９>", 2, 0, "X", "a,X,<_#９>")]
    [InlineData("a,b", 2, 2, "x,y", "a,x,y")]
    [InlineData("a,b", 2, 2, "x\"y", "a,\"x\"\"y\"")]
    [InlineData("a,b", 2, 1, "x\"y", "a,x\"y")]
    [InlineData("a,\"unclosed,b", 2, 0, "X", "a,X")]
    public async Task ColumnSplicingKeepsModeAndRawSuffixRules(
        string source, int column, int mode, string value, string expected)
    {
        string path = Fixture(source + "\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(18)], default);
        await session.InvokeAsync("FPUT",
            [id, S(value), N(1), N(column), N(mode)], default);
        Assert.Equal(S(expected + "\r\n"), await session.InvokeAsync(
            "FGET", [id, N(-2)], default));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteConversionAndLongNumericArgumentsAreInvariant(
        string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string path = Fixture("a,b\n");
            await using IScriptFileSession session =
                new ScriptFiles().CreateSession();
            await session.InvokeAsync("FOPEN", [S(path), N(18 | 256)], default);
            await session.InvokeAsync("FPUT",
                [S(new string('0', 1000) + "1.9e+0"), N(1.5),
                    S(" 3.9e+0 "), N(3.9), N(0.9)], default);
            Assert.Equal(S("a,b\r\n\r\n\t\t1.5\r\n"),
                await session.InvokeAsync("FGET", [N(1), N(-2)], default));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void UnsupportedKindsAreRejectedInEveryPosition(ValueKind kind)
    {
        for (int index = 0; index < 5; index++)
        {
            Value[] arguments = [N(1), S("value"), N(1), N(1), N(0)];
            arguments[index] = new Value(kind);
            Assert.Throws<InvalidOperationException>(() =>
                FileFunctions.Validate("FPUT", arguments));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteNumbersAreRejectedInEveryPosition(double number)
    {
        for (int index = 0; index < 5; index++)
        {
            Value[] arguments = [N(1), S("value"), N(1), N(1), N(0)];
            arguments[index] = new Value(ValueKind.Number, number);
            Assert.Throws<InvalidOperationException>(() =>
                FileFunctions.Validate("FPUT", arguments));
        }
    }

    [Fact]
    public async Task EmptyValueAndOptionalOmissionRetainAppendSemantics()
    {
        string path = Fixture("");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.Empty, new(ValueKind.Omitted),
                new(ValueKind.Omitted), new(ValueKind.Omitted)], default);
        Assert.Equal(N(1), await session.InvokeAsync("FGET", [id, N(-1)], default));
        Assert.Equal(S("\r\n"), await session.InvokeAsync("FGET", [id, N(-2)], default));
        await session.InvokeAsync("FPUT", [id, Value.Empty, N(-2)], default);
        Assert.Equal(N(0), await session.InvokeAsync("FGET", [id, N(-1)], default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(5)]
    public async Task CancelledPreparationKeepsBufferAndAllowsRetry(int row)
    {
        string path = Fixture("original\r\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync("FPUT", [id, S("new\ntext"), N(row)],
                cancellation.Token).AsTask());
        Assert.Equal(S("original\r\n"), await session.InvokeAsync(
            "FGET", [id, N(-2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FPUT", [id, S("retry"), N(1)], default);
        Assert.Equal(S("retry\r\n"), await session.InvokeAsync(
            "FGET", [id, N(-2)], default));
    }

    [Fact]
    public async Task InvalidInsertAndReadOnlyWriteKeepExistingBuffer()
    {
        string path = Fixture("original\r\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value read = await session.InvokeAsync("FOPEN", [S(path)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FPUT", [read, S("X")], default).AsTask());
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FPUT", [id, S("X"), N(3), N(-1)], default).AsTask());
        Assert.Equal(S("original\r\n"), await session.InvokeAsync(
            "FGET", [id, N(-2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200_002)]
    public async Task LargeColumnUpdateDoesNotAllocateEveryField(int column)
    {
        string source = "first," + string.Concat(
            Enumerable.Repeat("field,", 200_000)) + "last";
        string path = Fixture(source + "\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        long start = GC.GetAllocatedBytesForCurrentThread();
        await session.InvokeAsync("FPUT", [id, S("X"), N(1), N(column)], default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.InRange(allocated, 0, source.Length * 6L + 256 * 1024);
        Assert.Equal(S("X"), await session.InvokeAsync("FGET",
            [id, N(1), N(column)], default));
        Assert.Equal(N(1), await session.InvokeAsync("FGET", [id, N(-1)], default));
    }

    [Theory]
    [InlineData(2, 0, 0, "X", ",X")]
    [InlineData(3, 2, 0, "a\"b", ",,\"a\"\"b\"")]
    [InlineData(3, 0, 256, "X", "\t\tX")]
    public async Task AppendHonorsColumnsAndQuoteMode(int column,
        int quoteMode, int tab, string value, string expected)
    {
        string path = Fixture("old\r\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(18 | tab)], default);
        await session.InvokeAsync("FPUT",
            [id, S(value), N(0), N(column), N(quoteMode)], default);
        Assert.Equal(S("old\r\n" + expected + "\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
    }

    [Theory]
    [InlineData(0, 0, 3, "first\r\nlast\r\na\nb\r\n")]
    [InlineData(1, 0, 2, "a\nb\r\nlast\r\n")]
    [InlineData(1, -1, 3, "a\nb\r\nfirst\r\nlast\r\n")]
    public async Task EmbeddedNewlinesStayInsideOneLogicalEntry(
        int row, int column, int count, string expected)
    {
        string path = Fixture("first\r\nlast\r\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        await session.InvokeAsync("FPUT", [id, S("a\nb"), N(row), N(column)], default);
        Assert.Equal(N(count), await session.InvokeAsync("FGET", [id, N(-1)], default));
        Assert.Equal(S(expected), await session.InvokeAsync("FGET", [id, N(-2)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(expected, File.ReadAllText(path));
    }
}
