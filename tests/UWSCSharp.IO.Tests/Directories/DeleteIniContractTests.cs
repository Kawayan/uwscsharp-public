using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class DeleteIniContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-deleteini-" + Guid.NewGuid());

    public DeleteIniContractTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(directory, true);
    }

    private string PathOf(string name = "settings.ini") => Path.Combine(directory, name);

    private static Value S(string text) => Value.String(text);

    [Fact]
    public void EveryIniFunctionRejectsLeadingAndTrailingNameNewlines()
    {
        foreach (string function in new[] { "READINI", "WRITEINI", "DELETEINI" })
        {
            foreach (string text in new[] { "s\n", "\rs", "\r\ns\r\n" })
            {
                Value[] section = function == "WRITEINI"
                    ? [S(text), S("k"), S("v"), S(PathOf())] : [S(text), S("k"), S(PathOf())];
                Value[] key = function == "WRITEINI"
                    ? [S("s"), S(text), S("v"), S(PathOf())] : [S("s"), S(text), S(PathOf())];
                Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(function, section));
                Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(function, key));
            }
        }
    }

    [Theory]
    [InlineData(0, " ")]
    [InlineData(0, "a[b")]
    [InlineData(0, "a]b")]
    [InlineData(0, "s\n")]
    [InlineData(0, "s\0")]
    [InlineData(1, " ")]
    [InlineData(1, ";x")]
    [InlineData(1, " #x")]
    [InlineData(1, "[x")]
    [InlineData(1, "a=b")]
    [InlineData(1, "k\n")]
    [InlineData(1, "k\0")]
    [InlineData(2, "path\0")]
    public async Task InvalidFieldsCannotCreateOrChangeFiles(int position, string text)
    {
        string path = PathOf();
        Value[] args = [S("s"), S("k"), S(path)];
        args[position] = S(text);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("DELETEINI", args, default).AsTask());
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        File.WriteAllText(path, "[s]\nk=old\n");
        byte[] before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("DELETEINI", args, default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void UnsupportedValuesAndNonfiniteNumbersAreRejected(ValueKind kind)
    {
        for (int i = 0; i < 3; i++)
        {
            Value[] args = [S("s"), S("k"), S(PathOf())];
            args[i] = new Value(kind);
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("DELETEINI", args));
            foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                args[i] = new Value(ValueKind.Number, number);
                Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("DELETEINI", args));
            }
        }
    }

    [Fact]
    public async Task FirstKeyAndSectionAreDeletedWithoutChangingOtherLines()
    {
        string path = PathOf();
        const string source = ";keep\r\n[ s ]\n k =one\rk=two\n#keep\r\n[s]\nk=three\n[t]\nz=stay";
        File.WriteAllText(path, source, new UTF8Encoding(false));
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Empty, await session.InvokeAsync("DELETEINI", [S(" S "), S(" K "), S(path)], default));
        Assert.Equal(";keep\r\n[ s ]\nk=two\n#keep\r\n[s]\nk=three\n[t]\nz=stay", File.ReadAllText(path));
        await session.InvokeAsync("DELETEINI", [S("s"), Value.Empty, S(path)], default);
        Assert.Equal(";keep\r\n[s]\nk=three\n[t]\nz=stay", File.ReadAllText(path));
        Assert.Equal(S("three"), await session.InvokeAsync("READINI", [S("s"), S("k"), S(path)], default));
    }

    [Theory]
    [InlineData("cp932")]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task EncodingPreambleAndEmptyFileArePreserved(string kind)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = kind switch
        {
            "cp932" => Encoding.GetEncoding(932),
            "utf8" => new UTF8Encoding(false),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true)
        };
        string path = PathOf();
        File.WriteAllText(path, ";日本語\n[s]\nk=remove\nx=変更", encoding);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("DELETEINI", [S("s"), S("k"), S(path)], default);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(";日本語\n[s]\nx=変更")];
        Assert.Equal(expected, File.ReadAllBytes(path));
        File.WriteAllText(path, "[s]\nk=only", encoding);
        await session.InvokeAsync("DELETEINI", [S("s"), S(""), S(path)], default);
        Assert.True(File.Exists(path));
        Assert.Equal(encoding.GetPreamble(), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task MissingTargetsAreNoopsEvenWhenReplacementIsForbidden()
    {
        string path = PathOf();
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("DELETEINI", [S("s"), S("k"), S(path)], default);
        await session.InvokeAsync("DELETEINI", [S("s"), S(""), S(PathOf("missing/path.ini"))], default);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        File.WriteAllText(path, "[s]\nk=stay\n");
        byte[] before = File.ReadAllBytes(path);
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await session.InvokeAsync("DELETEINI", [S("s"), S("absent"), S(path)], default);
        await session.InvokeAsync("DELETEINI", [S("absent"), S(""), S(path)], default);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task CommitFailurePreservesOriginalCleansStagingAndAllowsRetry()
    {
        string path = PathOf();
        string original = "[s]\nk=delete\nx=" + new string('x', 2_000_000);
        File.WriteAllText(path, original);
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => session.InvokeAsync("DELETEINI",
                [S("s"), S("k"), S(path)], default).AsTask());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
        }

        await session.InvokeAsync("DELETEINI", [S("s"), S("k"), S(path)], default);
        Assert.Equal("[s]\nx=" + new string('x', 2_000_000), File.ReadAllText(path));
    }

    [Fact]
    public async Task DirectoryReadonlyAndMalformedDataFailWithoutDamage()
    {
        string path = PathOf();
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.InvokeAsync("DELETEINI",
            [S("s"), S("k"), S(path)], default).AsTask());
        Directory.Delete(path);
        File.WriteAllText(path, "[s]\nk=old\n");
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.InvokeAsync("DELETEINI",
            [S("s"), S("k"), S(path)], default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, [0xff, 0xfe, 0x41]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => session.InvokeAsync("DELETEINI",
            [S("s"), S("k"), S(path)], default).AsTask());
        Assert.Equal(new byte[] { 0xff, 0xfe, 0x41 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task WriteAndDeleteAcrossSessionsPreserveEveryCompletedUpdate()
    {
        string path = PathOf();
        File.WriteAllText(path, "[s]\n" + string.Join("\n", Enumerable.Range(0, 32).Select(i => "drop" + i + "=old")) + "\n");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            await session.InvokeAsync("DELETEINI", [S("s"), S("drop" + i), S(path)], default);
            await session.InvokeAsync("WRITEINI", [S("s"), S("keep" + i), Value.Numeric(i), S(path)], default);
        }));
        await using IScriptFileSession reader = new ScriptFiles().CreateSession();
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(S(""), await reader.InvokeAsync("READINI", [S("s"), S("drop" + i), S(path)], default));
            Assert.Equal(Value.Numeric(i), await reader.InvokeAsync("READINI", [S("s"), S("keep" + i), S(path)], default));
        }

        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task DefaultsOptionalKeysAndPreCancellationHaveDefinedEffects()
    {
        string path = PathOf("task.INI");
        await using IScriptFileSession session = new ScriptFiles("other/task.uws", directory).CreateSession();
        foreach (Value[] args in new[]
        {
            new[] { S("s") },
            [S("s"), new(ValueKind.Omitted)],
            [S("s"), S("")],
            [S("s"), Value.Empty, S("")],
            [S("s"), new(ValueKind.Omitted), S(PathOf("task"))]
        })
        {
            File.WriteAllText(path, "[s]\nk=old");
            await session.InvokeAsync("DELETEINI", args, default);
            Assert.Empty(File.ReadAllBytes(path));
        }

        File.WriteAllText(path, "[s]\nk=old");
        byte[] before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("DELETEINI",
            [S("s"), S("k"), S(path)], cancellation.Token).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("DELETEINI",
            [S("s"), S("k"), S(PathOf("absent.ini"))], cancellation.Token).AsTask());
        Assert.False(File.Exists(PathOf("absent.ini")));
        await using IScriptFileSession noContext = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noContext.InvokeAsync("DELETEINI", [S("s")], default).AsTask());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task FiniteNumericNamesAreCultureIndependent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            File.WriteAllText(PathOf(), "[1.5]\n2.5=remove\nkeep=stay");
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            await session.InvokeAsync("DELETEINI", [Value.Numeric(1.5), Value.Numeric(2.5), S(PathOf())], default);
            Assert.Equal("[1.5]\nkeep=stay", File.ReadAllText(PathOf()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
