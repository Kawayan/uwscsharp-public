using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.IO.Tests;

public sealed class FopenContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-fopen-" + Guid.NewGuid());
    private readonly ITestOutputHelper output;

    public FopenContractTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このfixtureが作成した専用ディレクトリだけを削除します。
        Directory.Delete(directory, true);
    }

    private string PathOf(string name = "data.txt") => Path.Combine(directory, name);

    private static Value N(double value) => Value.Numeric(value);

    private static Value S(string value) => Value.String(value);

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("cp932")]
    [InlineData("ambiguous")]
    public async Task EncodingRulesProduceCurrentBufferAndKeepBytes(string kind)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = kind switch
        {
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(false, true)
        };
        string text = kind == "ambiguous" ? "©" : "日本語\r\nnext\n";
        byte[] before = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        File.WriteAllBytes(PathOf(), before);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf())], default);
        Assert.Equal(N(1), id);
        Assert.Equal(S(kind == "ambiguous" ? "©\r\n" : "日本語\r\nnext\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(before, File.ReadAllBytes(PathOf()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheFirstBomIsRemoved(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        File.WriteAllBytes(PathOf(), [.. encoding.GetPreamble(),
            .. encoding.GetBytes("\ufeffvalue\n")]);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf())], default);
        Assert.Equal(S("\ufeffvalue"), await session.InvokeAsync("FGET",
            [id, N(1)], default));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(16 | 512)]
    public async Task WriteOnlyDoesNotDecodeInvalidExistingBytes(int mode)
    {
        byte[] before = [0xef, 0xbb, 0xbf, 0xff];
        File.WriteAllBytes(PathOf(), before);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf()), N(mode)], default);
        Assert.Equal(N(1), id);
        Assert.Equal(N(0), await session.InvokeAsync("FGET", [id, N(-1)], default));
        if ((mode & 512) == 0)
        {
            Assert.Equal(before, File.ReadAllBytes(PathOf()));
        }

        await session.InvokeAsync("FPUT", [id, S("saved")], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("saved\r\n", File.ReadAllText(PathOf()));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(18 | 512)]
    public async Task MissingReadWriteCreatesEmptyFileAndPublishesId(int mode)
    {
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf()), N(mode)], default);
        Assert.Equal(N(1), id);
        Assert.Equal((mode & 512) != 0, File.Exists(PathOf()));
        Assert.Equal(N(0), await session.InvokeAsync("FGET", [id, N(-1)], default));
        await session.InvokeAsync("FPUT", [id, S("created")], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("created\r\n", File.ReadAllText(PathOf()));
    }

    [Fact]
    public async Task NonExclusiveReadCanShareWithExistingWriter()
    {
        File.WriteAllText(PathOf(), "old");
        using var writer = new FileStream(PathOf(), FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf())], default);
        Assert.Equal(N(1), id);
        Assert.Equal(S("old"), await session.InvokeAsync("FGET", [id, N(1)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(2 | 512)]
    public async Task InvalidBomReleasesHandleAndDoesNotConsumeId(int mode)
    {
        byte[] before = [0xff, 0xfe, 0x41];
        File.WriteAllBytes(PathOf(), before);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(N(-1), await session.InvokeAsync("FOPEN",
            [S(PathOf()), N(mode)], default));
        using (FileStream probe = File.Open(PathOf(), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(before.Length, probe.Length);
        }

        File.WriteAllText(PathOf(), "fixed");
        Assert.Equal(N(1), await session.InvokeAsync("FOPEN", [S(PathOf())], default));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(2 | 512)]
    public async Task RequestedCancellationHasAValidPublicationOutcome(int mode)
    {
        File.WriteAllText(PathOf(), string.Concat(Enumerable.Repeat("value\n", 300_000)));
        byte[] before = File.ReadAllBytes(PathOf());
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        using var cancellation = new CancellationTokenSource();
        ValueTask<Value> opening = session.InvokeAsync("FOPEN",
            [S(PathOf()), N(mode)], cancellation.Token);
        bool pending = !opening.IsCompleted;
        cancellation.Cancel();
        int expectedId;
        try
        {
            Value id = await opening;
            Assert.Equal(N(1), id);
            output.WriteLine($"open-cancel=published;pending={pending}");
            await session.InvokeAsync("FCLOSE", [id], default);
            expectedId = 2;
        }
        catch (OperationCanceledException)
        {
            Assert.True(pending);
            output.WriteLine("open-cancel=unpublished;pending=True");
            expectedId = 1;
        }

        using (FileStream probe = File.Open(PathOf(), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(before.Length, probe.Length);
        }

        Assert.Equal(before, File.ReadAllBytes(PathOf()));
        Assert.Equal(N(expectedId), await session.InvokeAsync("FOPEN",
            [S(PathOf())], default));
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void UnsupportedKindsAreRejectedInBothPositions(ValueKind kind)
    {
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(
            "FOPEN", [new(kind)]));
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(
            "FOPEN", [S(PathOf()), new(kind)]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteNumbersAreRejectedInBothPositions(double number)
    {
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(
            "FOPEN", [new(ValueKind.Number, number)]));
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(
            "FOPEN", [S(PathOf()), new(ValueKind.Number, number)]));
    }

    [Fact]
    public async Task LongModeTextAndOmissionKeepDefaultReadAndFiniteRules()
    {
        File.WriteAllText(PathOf(), "kept");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [S(PathOf()), S(new string('0', 1000) + "2.9e+0")], default);
        Assert.Equal(S("kept"), await session.InvokeAsync("FGET", [id, N(1)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(N(2), await session.InvokeAsync("FOPEN",
            [S(PathOf()), new(ValueKind.Omitted)], default));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("cp932")]
    public async Task AutoReadWriteKeepsEncodingAndBom(string kind)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = kind switch
        {
            "utf8bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(false, true)
        };
        byte[] before = [.. encoding.GetPreamble(), .. encoding.GetBytes("日本語\r\n")];
        File.WriteAllBytes(PathOf(), before);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf()), N(6)], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(before, File.ReadAllBytes(PathOf()));
    }

    [Fact]
    public async Task AutoAnsiReadWriteFallsBackToUtf8WithoutLosingCharacters()
    {
        File.WriteAllBytes(PathOf(), [0x93, 0xfa, 0x96, 0x7b]);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN", [S(PathOf()), N(6)], default);
        await session.InvokeAsync("FPUT", [id, S("😀")], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("日本\r\n😀\r\n"),
            File.ReadAllBytes(PathOf()));
    }
}
