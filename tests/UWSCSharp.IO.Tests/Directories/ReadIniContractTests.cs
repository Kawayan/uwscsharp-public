using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Testing;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class ReadIniContractTests : IDisposable
{
    private readonly TemporaryTestDirectory temporary =
        new("UWSCSharp-readini-");

    private string directory => temporary.Path;

    public void Dispose() => temporary.Dispose();

    [Theory]
    [InlineData("001", 1)]
    [InlineData(" 1.5e2 ", 150)]
    [InlineData("-0.9", -0.9)]
    [InlineData("0.0001", 0.0001)]
    public async Task FiniteNumbersUseTheSharedGrammarAndDoNotChangeTheFile(string text, double expected)
    {
        Assert.Equal(Value.Numeric(expected), await ReadValue(text));
    }

    [Theory]
    [InlineData("$10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,000")]
    [InlineData("1e+")]
    [InlineData("\"001\"")]
    [InlineData("'text'")]
    [InlineData("a=b;c#d")]
    [InlineData("日本語😀")]
    [InlineData("")]
    [InlineData("a\0b")]
    public async Task NonNumericValuesRemainStringsIncludingQuotesAndNul(string text)
    {
        Assert.Equal(Value.String(text), await ReadValue(text));
    }

    [Theory]
    [InlineData("cp932")]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task ExistingEncodingAndAllLineEndingsAreReadWithoutRewriting(string kind)
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
        foreach (string newline in new[] { "\r\n", "\n", "\r" })
        {
            string path = Path.Combine(directory, "encoding.ini");
            File.WriteAllText(path, "[s]" + newline + "k=日本語" + newline, encoding);
            byte[] before = File.ReadAllBytes(path);
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            Assert.Equal(Value.String("日本語"), await session.InvokeAsync("READINI",
                [Value.String("s"), Value.String("k"), Value.String(path)], default));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task FirstSectionAndFirstKeyWinAndQueriesTrimNames()
    {
        string path = Path.Combine(directory, "duplicate.ini");
        File.WriteAllText(path, ";before\n#comment\n[ Main ]\n x = first \nx=second\n[main]\nx=third\ny=later\n", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.String("first"), await session.InvokeAsync("READINI",
            [Value.String(" main "), Value.String(" X "), Value.String(path)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync("READINI",
            [Value.String("main"), Value.String("y"), Value.String(path)], default));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task MissingFileSectionAndKeyAreEmptyWithoutCreatingAnything()
    {
        string missing = Path.Combine(directory, "absent", "missing.ini");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.String(""), await session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(missing)], default));
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        string path = Path.Combine(directory, "present.ini");
        File.WriteAllText(path, "[other]\nx=value\n", new UTF8Encoding(false));
        Assert.Equal(Value.String(""), await session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(path)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync("READINI",
            [Value.String("other"), Value.String("k"), Value.String(path)], default));
    }

    [Fact]
    public async Task DirectoryLockAndMalformedEncodingAreFailuresInsteadOfMissingValues()
    {
        string folder = Path.Combine(directory, "folder.ini");
        Directory.CreateDirectory(folder);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(folder)], default).AsTask());
        string path = Path.Combine(directory, "locked.ini");
        File.WriteAllText(path, "[s]\nk=value\n");
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => session.InvokeAsync("READINI",
                [Value.String("s"), Value.String("k"), Value.String(path)], default).AsTask());
        }

        Assert.Equal(Value.String("value"), await session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(path)], default));
        File.WriteAllBytes(path, [0xff, 0xfe, 0x41]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(path)], default).AsTask());
        Assert.Equal(new byte[] { 0xff, 0xfe, 0x41 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task DefaultContextAndExtensionRulesAreExplicit()
    {
        string path = Path.Combine(directory, "task.INI");
        File.WriteAllText(path, "[s]\nk=7\n");
        await using IScriptFileSession session = new ScriptFiles("elsewhere/task.uws", directory).CreateSession();
        foreach (Value[] arguments in new[] {
            new[] { Value.String("s"), Value.String("k") },
            [Value.String("s"), Value.String("k"), new(ValueKind.Omitted)],
            [Value.String("s"), Value.String("k"), Value.String("")],
            [Value.String("s"), Value.String("k"), Value.String(Path.Combine(directory,"task"))] })
        {
            Assert.Equal(Value.Numeric(7), await session.InvokeAsync("READINI", arguments, default));
        }

        await using IScriptFileSession noContext = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noContext.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k")], default).AsTask());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task NumbersAndOrdinalNamesDoNotUseAmbientCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(Value.Numeric(1.5), await ReadValue("1.5"));
            Assert.Equal(Value.String("1,5"), await ReadValue("1,5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task LargeValueLongNumberAndCancelledReadRespectOwnership()
    {
        string longNumber = new('0', 1000);
        Assert.Equal(Value.Numeric(7), await ReadValue(longNumber + "7"));
        string large = new('x', 2_000_000);
        Assert.Equal(Value.String(large), await ReadValue(large));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string missing = Path.Combine(directory, "cancelled.ini");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(missing)], cancellation.Token).AsTask());
        Assert.False(File.Exists(missing));
    }

    private async Task<Value> ReadValue(string value)
    {
        string path = Path.Combine(directory, "value.ini");
        File.WriteAllText(path, "[s]\nk=" + value + "\n", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value result = await session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("k"), Value.String(path)], default);
        Assert.Equal(before, File.ReadAllBytes(path));
        return result;
    }
}
