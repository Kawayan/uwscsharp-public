using System.Globalization;
using System.Reflection;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class FdelLineContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-fdelline-" + Guid.NewGuid());

    public FdelLineContractTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    private string PathOf(string name = "data.txt") => Path.Combine(directory, name);

    private static Value S(string text) => Value.String(text);

    private static Value N(int number) => Value.Numeric(number);

    [Theory]
    [InlineData(1, "two\r\nthree\r\n")]
    [InlineData(2, "one\r\nthree\r\n")]
    [InlineData(3, "one\r\ntwo\r\n")]
    public async Task DeletesExactlyOneRowAndOnlyCloseSaves(int row, string expected)
    {
        string path = PathOf();
        byte[] before = Encoding.UTF8.GetBytes("one\ntwo\rthree");
        File.WriteAllBytes(path, before);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(2 | 16)], default);
        Assert.Equal(Value.Empty, await session.InvokeAsync("FDELLINE", [id, N(row)], default));
        Assert.Equal(N(2), await session.InvokeAsync("FGET", [id, N(-1)], default));
        Assert.Equal(S(expected), await session.InvokeAsync("FGET", [id, N(-2)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("1e+300")]
    [InlineData("$1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,000")]
    public async Task InvalidValuesPreserveBufferAndFile(string value)
    {
        string path = PathOf();
        File.WriteAllText(path, "one\r\ntwo\r\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        foreach (Value[] args in new[] { new[] { S(value), N(1) }, [id, S(value)] })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("FDELLINE", args, default).AsTask());
            Assert.Equal(S("one\r\ntwo\r\n"), await session.InvokeAsync("FGET", [id, N(-2)], default));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData(ValueKind.Empty)]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void UnsupportedKindsAndNonfiniteNumbersAreRejected(ValueKind kind)
    {
        foreach (Value[] args in new[] { new[] { new Value(kind), N(1) }, [N(1), new Value(kind)] })
        {
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("FDELLINE", args));
        }

        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("FDELLINE", [new(ValueKind.Number, number), N(1)]));
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("FDELLINE", [N(1), new(ValueKind.Number, number)]));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteExponentFractionsAndLongStringsUseInvariantRules(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string path = PathOf();
            File.WriteAllText(path, "one\ntwo\nthree\nfour");
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
            await session.InvokeAsync("FDELLINE", [Value.Numeric(id.Number + 0.9), S(" 2.9e+0 ")], default);
            await session.InvokeAsync("FDELLINE", [S("000" + id.Number.ToString(CultureInfo.InvariantCulture)), S(new string('0', 1000) + "2")], default);
            Assert.Equal(S("one\r\nfour\r\n"), await session.InvokeAsync("FGET", [id, N(-2)], default));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ModeRangeEmptyAndClosedIdsFailWithoutAdditionalMutation()
    {
        string path = PathOf();
        File.WriteAllText(path, "one\r\n");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value read = await session.InvokeAsync("FOPEN", [S(path)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("FDELLINE", [read, N(1)], default).AsTask());
        await session.InvokeAsync("FCLOSE", [read], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("FDELLINE", [read, N(1)], default).AsTask());
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("FDELLINE", [id, N(2)], default).AsTask());
        await session.InvokeAsync("FDELLINE", [id, N(1)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("FDELLINE", [id, N(1)], default).AsTask());
        Assert.Equal(N(0), await session.InvokeAsync("FGET", [id, N(-1)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(2 | 8, "cp932")]
    [InlineData(2 | 16, "utf8")]
    [InlineData(2 | 32, "utf8bom")]
    [InlineData(2 | 64, "utf16")]
    [InlineData(2 | 16 | 128, "nocr")]
    [InlineData(2 | 16 | 256, "tab")]
    [InlineData(2 | 16 | 512, "exclusive")]
    public async Task WholeRowsRespectExistingSaveModes(int mode, string kind)
    {
        string path = PathOf();
        File.WriteAllText(path, "日本語,keep\n削除\tx\nend\0😀", new UTF8Encoding(true));
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(mode)], default);
        await session.InvokeAsync("FDELLINE", [id, N(3)], default);
        await session.InvokeAsync("FDELLINE", [id, N(2)], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = kind switch
        {
            "cp932" => Encoding.GetEncoding(932),
            "utf8bom" => new UTF8Encoding(true),
            "utf16" => new UnicodeEncoding(false, true),
            _ => new UTF8Encoding(false)
        };
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("日本語,keep" + (kind == "nocr" ? "" : "\r\n"))];
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CloseEncodingFailureKeepsOriginalAndReleasesExclusiveHandle()
    {
        string path = PathOf();
        File.WriteAllText(path, "delete\n😀\n", new UTF8Encoding(true));
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(2 | 8 | 512)], default);
        await session.InvokeAsync("FDELLINE", [id, N(1)], default);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => session.InvokeAsync("FCLOSE", [id], default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        using FileStream lease = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ExhaustedIdsNeverWrapOrReuseAndLastIdWorksWithDeleteLine()
    {
        string path = PathOf();
        File.WriteAllText(path, "one\ntwo\n");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        FieldInfo nextId = session.GetType().GetField("nextId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        nextId.SetValue(session, int.MaxValue);
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        Assert.Equal(N(int.MaxValue), id);
        await session.InvokeAsync("FDELLINE", [id, N(1)], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("two\r\n", File.ReadAllText(path));
        Assert.Equal(N(-1), await session.InvokeAsync("FOPEN", [S(PathOf("new.txt")), N(16 | 512)], default));
        Assert.False(File.Exists(PathOf("new.txt")));
        Assert.Equal(N(1), await session.InvokeAsync("FOPEN", [S(path), N(1)], default));
    }

    [Fact]
    public async Task CancelledDeletionPreservesBufferAndAllowsRetry()
    {
        string path = PathOf();
        string source = "first\n" + string.Concat(Enumerable.Repeat("value\n", 10_000));
        File.WriteAllText(path, source);
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(path), N(18)], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("FDELLINE", [id, N(1)], cancellation.Token).AsTask());
        Assert.Equal(N(10_001), await session.InvokeAsync("FGET", [id, N(-1)], default));
        Assert.Equal(S("first"), await session.InvokeAsync("FGET", [id, N(1)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
        await session.InvokeAsync("FDELLINE", [id, N(1)], default);
        Assert.Equal(N(10_000), await session.InvokeAsync("FGET", [id, N(-1)], default));
    }
}
