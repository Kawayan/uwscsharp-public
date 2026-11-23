using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class WriteIniContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-writeini-" + Guid.NewGuid());

    public WriteIniContractTests()
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

    [Theory]
    [InlineData(";x")]
    [InlineData("#x")]
    [InlineData(" ;x")]
    [InlineData(" #x")]
    [InlineData("[x")]
    [InlineData(" [x]")]
    [InlineData(" ")]
    [InlineData("x=y")]
    [InlineData("x\ny")]
    [InlineData("x\0y")]
    public async Task InvalidKeysCannotCreateOrChangeAFile(string key)
    {
        string path = PathOf();
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S(key), S("]"), S(path)], default).AsTask());
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "[s]\nk=old\n");
        byte[] before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S(key), S("]"), S(path)], default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Theory]
    [InlineData(0, " ")]
    [InlineData(0, "a[b")]
    [InlineData(0, "a]b")]
    [InlineData(0, "a\rb")]
    [InlineData(0, "a\0b")]
    [InlineData(2, "a\nb")]
    [InlineData(2, "a\rb")]
    [InlineData(2, "a\0b")]
    [InlineData(3, "a\0b")]
    public async Task InvalidFieldsFailBeforeOpeningTheTarget(int position, string text)
    {
        Value[] arguments = [S("s"), S("k"), S("v"), S(PathOf())];
        arguments[position] = S(text);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("WRITEINI", arguments, default).AsTask());
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void UnsupportedValuesAreRejectedInEveryPosition(ValueKind kind)
    {
        for (int position = 0; position < 4; position++)
        {
            Value[] arguments = [S("s"), S("k"), S("v"), S(PathOf())];
            arguments[position] = new Value(kind);
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("WRITEINI", arguments));
        }
    }

    [Fact]
    public async Task FirstSectionAndKeyAreChangedAndUnrelatedLinesStayExact()
    {
        string path = PathOf();
        File.WriteAllText(path, ";keep\r\n[ s ]\n k =old\rx=stay\n k =second\r\n[s]\nk=later", new UTF8Encoding(false));
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Empty, await session.InvokeAsync("WRITEINI",
            [S(" S "), S(" K "), S("a=b;c#d"), S(path)], default));
        Assert.Equal(";keep\r\n[ s ]\nK=a=b;c#d\rx=stay\n k =second\r\n[s]\nk=later", File.ReadAllText(path));
        Assert.Equal(S("a=b;c#d"), await session.InvokeAsync("READINI",
            [S("s"), S("k"), S(path)], default));
    }

    [Theory]
    [InlineData("[s]\nk=old", "[s]\nk=new")]
    [InlineData("[s]\nx=stay", "[s]\nx=stay\nk=new\n")]
    [InlineData("[t]\nx=stay\n", "[t]\nx=stay\n[s]\nk=new\n")]
    [InlineData("", "[s]\r\nk=new\r\n")]
    [InlineData("[s]\nx=stay\r[t]\nk=other", "[s]\nx=stay\rk=new\n[t]\nk=other")]
    public async Task InsertionsAndUnterminatedLinesUseExplicitNewlineRules(string before, string after)
    {
        string path = PathOf();
        File.WriteAllText(path, before, new UTF8Encoding(false));
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("WRITEINI", [S("s"), S("k"), S("new"), S(path)], default);
        Assert.Equal(new UTF8Encoding(false).GetBytes(after), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("cp932")]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task EncodingAndPreambleArePreserved(string kind)
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
        File.WriteAllText(path, ";日本語\n[s]\nk=old\n", encoding);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("WRITEINI", [S("s"), S("k"), S("変更"), S(path)], default);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(";日本語\n[s]\nk=変更\n")];
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NewCp932AndUpgradeToUtf8HaveStrictConversion()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = PathOf();
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("WRITEINI", [S("s"), S("k"), S("日本語"), S(path)], default);
        Assert.Equal(Encoding.GetEncoding(932).GetBytes("[s]\r\nk=日本語\r\n"), File.ReadAllBytes(path));
        await session.InvokeAsync("WRITEINI", [S("s"), S("k"), S("😀"), S(path)], default);
        Assert.Equal(new UTF8Encoding(false).GetBytes("[s]\r\nk=😀\r\n"), File.ReadAllBytes(path));
        byte[] before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("\ud800"), S(path)], default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CommitFailurePreservesOriginalAndCleansStagingThenAllowsRetry()
    {
        string path = PathOf();
        File.WriteAllText(path, "[s]\nk=old\n");
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => session.InvokeAsync("WRITEINI",
                [S("s"), S("k"), S(new string('x', 2_000_000)), S(path)], default).AsTask());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
        }

        await session.InvokeAsync("WRITEINI", [S("s"), S("k"), S("retry"), S(path)], default);
        Assert.Equal("[s]\nk=retry\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task DirectoryReadonlyMissingParentAndMalformedInputFailWithoutDamage()
    {
        string path = PathOf();
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(path)], default).AsTask());
        Directory.Delete(path);
        File.WriteAllText(path, "[s]\nk=old\n");
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(path)], default).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, [0xff, 0xfe, 0x41]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(path)], default).AsTask());
        Assert.Equal(new byte[] { 0xff, 0xfe, 0x41 }, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(PathOf("missing/file.ini"))], default).AsTask());
        Assert.False(Directory.Exists(PathOf("missing")));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSessionsPreserveEveryCompletedKey()
    {
        string path = PathOf();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
        {
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            await session.InvokeAsync("WRITEINI", [S("s"), S("k" + index), Value.Numeric(index), S(path)], default);
        }));
        await using IScriptFileSession reader = new ScriptFiles().CreateSession();
        for (int index = 0; index < 32; index++)
        {
            Assert.Equal(Value.Numeric(index), await reader.InvokeAsync("READINI",
                [S("s"), S("k" + index), S(path)], default));
        }

        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task DefaultsEmptyValuesFiniteConversionAndCancellationAreExplicit()
    {
        string path = PathOf("task.INI");
        await using IScriptFileSession session = new ScriptFiles("other/task.uws", directory).CreateSession();
        foreach (Value[] args in new[]
        {
            new[] { S(" s "), S(" k "), Value.Empty },
            [S("s"), S("k"), S(""), new(ValueKind.Omitted)],
            [S("s"), S("k"), Value.Numeric(1.5), S("")],
            [S("s"), S("k"), S("001"), S(PathOf("task"))]
        })
        {
            await session.InvokeAsync("WRITEINI", args, default);
        }

        Assert.Equal("[s]\r\nk=001\r\n", File.ReadAllText(path));
        byte[] before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(path)], cancellation.Token).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("new"), S(PathOf("cancelled.ini"))], cancellation.Token).AsTask());
        Assert.False(File.Exists(PathOf("cancelled.ini")));
        await using IScriptFileSession noContext = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noContext.InvokeAsync("WRITEINI",
            [S("s"), S("k"), S("v")], default).AsTask());
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("WRITEINI",
                [S("s"), S("k"), new(ValueKind.Number, number), S(path)]));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task NumericFieldsAreInvariantAcrossCultures(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            await session.InvokeAsync("WRITEINI",
                [Value.Numeric(1.5), Value.Numeric(2.5), Value.Numeric(3.5), S(PathOf())], default);
            Assert.Equal("[1.5]\r\n2.5=3.5\r\n", File.ReadAllText(PathOf()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
