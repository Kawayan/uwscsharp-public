using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class FgetContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-fget-" + Guid.NewGuid());

    public FgetContractTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このfixtureが作成した専用ディレクトリだけを削除します。
        Directory.Delete(directory, true);
    }

    private static Value N(double value) => Value.Numeric(value);

    private static Value S(string value) => Value.String(value);

    private string Fixture(string text)
    {
        string path = Path.Combine(directory, Guid.NewGuid() + ".txt");
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    [Theory]
    [InlineData("<_#９>", 0, "<_#９>", false)]
    [InlineData(" <_#９>\t<_#９> ", 1, "<_#９>\t<_#９>", false)]
    [InlineData("\t123\t", 0, "123", true)]
    [InlineData("$10", 0, "$10", false)]
    [InlineData("1e+5000", 0, "1e+5000", false)]
    [InlineData("NaN", 0, "NaN", false)]
    [InlineData("Infinity", 0, "Infinity", false)]
    [InlineData("\"001\"", 0, "001", false)]
    [InlineData("\"001\"", 1, "\"001\"", false)]
    [InlineData("\"001\"", 2, "1", true)]
    [InlineData("\"a\"\"b\"", 0, "\"a\"\"b\"", false)]
    [InlineData("\"a\"\"b\"", 2, "a\"b", false)]
    [InlineData("\"unterminated", 0, "\"unterminated,tail,", false)]
    [InlineData("\"unterminated", 2, "\"unterminated,tail,", false)]
    [InlineData("\0abc\0", 0, "abc", false)]
    [InlineData("a\0b", 0, "a\0b", false)]
    [InlineData("日本語😀", 0, "日本語😀", false)]
    public async Task FormattingAndFiniteConversionPreserveExpectedKinds(
        string text, int mode, string expected, bool numeric)
    {
        string path = Fixture(text + ",tail,\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path)], default);
        Value result = await session.InvokeAsync("FGET",
            [id, N(1), N(1), N(mode)], default);
        Assert.Equal(numeric ? ValueKind.Number : ValueKind.String,
            result.Kind);
        Assert.Equal(expected, result.Display());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public async Task ColumnsQuotesAndTrailingEmptyFieldsRemainIndependent(
        int tab)
    {
        string separator = tab == 0 ? "," : "\t";
        string text = "first" + separator + "\"a" + separator
            + "b\"" + separator + "\"c\"\"d\"" + separator;
        string path = Fixture(text + "\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(2 | tab)], default);
        Assert.Equal(S(text), await session.InvokeAsync("FGET",
            [id, N(1), N(0)], default));
        Assert.Equal(S("a" + separator + "b"), await session.InvokeAsync(
            "FGET", [id, N(1), N(2)], default));
        Assert.Equal(S("c\"d"), await session.InvokeAsync("FGET",
            [id, N(1), N(3), N(2)], default));
        Assert.Equal(S("first"), await session.InvokeAsync("FGET",
            [id, N(1), N(1)], default));
        foreach (int column in new[] { 4, 5, int.MaxValue })
        {
            Assert.Equal(S(""), await session.InvokeAsync("FGET",
                [id, N(1), N(column)], default));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task LongNumericTextAndArgumentsUseInvariantRules(
        string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string path = Fixture(new string('0', 1000) + "2.5e+0\n");
            await using IScriptFileSession session =
                new ScriptFiles().CreateSession();
            await session.InvokeAsync("FOPEN", [S(path)], default);
            Assert.Equal(N(2.5), await session.InvokeAsync("FGET",
                [S(new string('0', 1000) + "1.9e+0"),
                    S(" 1.9e+0 "), N(1.9), N(0.9)], default));
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
        for (int index = 0; index < 4; index++)
        {
            Value[] arguments = [N(1), N(1), N(0), N(0)];
            arguments[index] = new Value(kind);
            Assert.Throws<InvalidOperationException>(() =>
                FileFunctions.Validate("FGET", arguments));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteNumbersAreRejectedInEveryPosition(double number)
    {
        for (int index = 0; index < 4; index++)
        {
            Value[] arguments = [N(1), N(1), N(0), N(0)];
            arguments[index] = new Value(ValueKind.Number, number);
            Assert.Throws<InvalidOperationException>(() =>
                FileFunctions.Validate("FGET", arguments));
        }
    }

    [Fact]
    public async Task SpecialRowsAndEmptyFileUseNormalizedBuffer()
    {
        string path = Fixture("one\ntwo\rthree\r\n\n");
        string empty = Fixture("");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path)], default);
        Value emptyId = await session.InvokeAsync("FOPEN",
            [S(empty)], default);
        Assert.Equal(N(4), await session.InvokeAsync("FGET",
            [id, N(-1)], default));
        Assert.Equal(S("one\r\ntwo\r\nthree\r\n\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        Assert.Equal(N(0), await session.InvokeAsync("FGET",
            [emptyId, N(-1)], default));
        Assert.Equal(S(""), await session.InvokeAsync("FGET",
            [emptyId, N(-2)], default));
        Assert.Equal(S(""), await session.InvokeAsync("FGET",
            [id, N(int.MaxValue)], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FGET", [id, N(-1), N(-1)], default).AsTask());
    }

    [Fact]
    public async Task ReadsObserveCurrentBufferAndDoNotChangeIt()
    {
        string path = Fixture("one,two\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(path), N(18)], default);
        await session.InvokeAsync("FPUT", [id, S("<_#９>"), N(1), N(2)], default);
        Assert.Equal(S("<_#９>"), await session.InvokeAsync("FGET",
            [id, N(1), N(2)], default));
        Assert.Equal(S("one,<_#９>\r\n"), await session.InvokeAsync("FGET",
            [id, N(-2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReadingOneColumnDoesNotAllocateAllOtherFields()
    {
        string path = Fixture("kept," + string.Concat(
            Enumerable.Repeat("field,", 200_000)) + "last\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path)], default);
        long start = GC.GetAllocatedBytesForCurrentThread();
        Value result = await session.InvokeAsync("FGET",
            [id, N(1), N(1)], default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(S("kept"), result);
        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Equal(S("last"), await session.InvokeAsync("FGET",
            [id, N(1), N(200_002)], default));
    }

    [Fact]
    public async Task CancelledReadKeepsIdAndAllowsRetry()
    {
        string path = Fixture("one,two\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path)], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync("FGET", [id, N(-2)],
                cancellation.Token).AsTask());
        Assert.Equal(S("two"), await session.InvokeAsync("FGET",
            [id, N(1), N(2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FCLOSE", [id], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("FGET", [id, N(1)], default).AsTask());
    }

    [Fact]
    public async Task WholeLineKeepsNonNumericWhitespaceAndLiteralMarkers()
    {
        const string text = "  \t<_#９> literal  ";
        string path = Fixture(text + "\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path)], default);
        Assert.Equal(S(text), await session.InvokeAsync("FGET",
            [id, N(1)], default));
        Assert.Equal(S(text + "\r\n"), await session.InvokeAsync("FGET",
            [id, N(-2)], default));
        Assert.Equal(S("\t<_#９> literal"), await session.InvokeAsync(
            "FGET", [id, N(1), N(1)], default));
    }
}
