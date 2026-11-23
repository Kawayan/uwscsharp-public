using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptFileTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-files-" + Guid.NewGuid());

    public ScriptFileTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このfixtureが作成した一意の専用ディレクトリだけを削除します。
        Directory.Delete(directory, true);
    }

    private string PathOf(string name = "file.txt") =>
        Path.Combine(directory, name);

    private static Value N(int number) => Value.Numeric(number);

    [Theory]
    [InlineData(8, "cp932")]
    [InlineData(16, "utf8")]
    [InlineData(32, "utf8bom")]
    [InlineData(64, "utf16")]
    [InlineData(4, "cp932")]
    public async Task ExplicitEncodingsRoundTripAndWaitUntilClose(
        int mode, string encodingName)
    {
        string path = PathOf();
        File.WriteAllText(path, "old");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(mode)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("日本語")], default);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(N(1), await session.InvokeAsync(
            "FCLOSE", [id], default));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = encodingName switch
        {
            "cp932" => Encoding.GetEncoding(932),
            "utf8bom" => new UTF8Encoding(true),
            "utf16" => new UnicodeEncoding(false, true),
            _ => new UTF8Encoding(false)
        };
        Assert.Equal(encoding.GetPreamble().Concat(
            encoding.GetBytes("日本語\r\n")), File.ReadAllBytes(path));
        Value read = await session.InvokeAsync("FOPEN",
            [Value.String(path)], default);
        Assert.Equal(Value.String("日本語"),
            await session.InvokeAsync("FGET", [read, N(1)], default));
    }

    [Fact]
    public async Task AutoEncodingKeepsCharactersOutsideCp932()
    {
        string path = PathOf();
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(4 | 128)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("😀")], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(new UTF8Encoding(false).GetBytes("😀"),
            File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RowsInsertPaddingAllTextAndReadWriteMode()
    {
        string path = PathOf();
        File.WriteAllText(path, "one\ntwo\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 16 | 128)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("insert"), N(2), N(-1)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("last"), N(5)], default);
        Assert.Equal(N(5), await session.InvokeAsync(
            "FGET", [id, N(-1)], default));
        Assert.Equal(Value.String("one\r\ninsert\r\ntwo\r\n\r\nlast\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        await session.InvokeAsync("FPUT",
            [id, Value.String("new\ntext\n"), N(-2)], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("new\r\ntext", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("123", 0, true, "123")]
    [InlineData("\"123\"", 0, false, "123")]
    [InlineData("\"123\"", 1, false, "\"123\"")]
    [InlineData("\"123\"", 2, true, "123")]
    [InlineData("\"a\"\"b\"", 0, false, "\"a\"\"b\"")]
    [InlineData("\"a\"\"b\"", 2, false, "a\"b")]
    [InlineData("ab\"c,d\"", 0, false, "ab\"c,d\"")]
    [InlineData("  \t  ", 0, false, "\t")]
    [InlineData("<_#９>", 0, false, "<_#９>")]
    [InlineData("\t123\t", 0, true, "123")]
    public async Task CsvQuotedValuesKeepTypesAndStaticFormatting(
        string field, int mode, bool numeric, string expected)
    {
        string path = PathOf();
        File.WriteAllText(path, field + ",tail,\r\n");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path)], default);
        Value value = await session.InvokeAsync("FGET",
            [id, N(1), N(1), N(mode)], default);
        Assert.Equal(numeric ? ValueKind.Number : ValueKind.String,
            value.Kind);
        Assert.Equal(expected, value.Display());
        Assert.Equal(Value.String("tail"), await session.InvokeAsync(
            "FGET", [id, N(1), N(2)], default));
        Assert.Equal(value, await session.InvokeAsync("FGET",
            [id, N(1), N(1), N(mode)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "FGET", [id, N(1), N(3)], default));
    }

    [Fact]
    public async Task TabEmptyLinesOutOfRangeAndUtf16BigEndian()
    {
        string path = PathOf();
        File.WriteAllText(path, "\r\na\t\"b\tc\"\t\r\n",
            new UnicodeEncoding(true, true));
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 256)], default);
        Assert.Equal(N(2), await session.InvokeAsync(
            "FGET", [id, N(-1)], default));
        Assert.Equal(Value.String("b\tc"), await session.InvokeAsync(
            "FGET", [id, N(2), N(2)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "FGET", [id, N(3)], default));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("exit")]
    [InlineData("exitexit")]
    [InlineData("error")]
    [InlineData("cancel")]
    public async Task InterpreterClosesBufferedWritesOnEveryExit(string ending)
    {
        string path = PathOf();
        string source = $"id=FOPEN(\"{path}\", F_WRITE8)\n"
            + "FPUT(id, \"saved\")\nPRINT \"pending\"\n"
            + (ending switch
            {
                "exit" => "EXIT",
                "exitexit" => "EXITEXIT",
                "error" => "PRINT 1/0",
                "cancel" => "PRINT \"unexpected\"",
                _ => ""
            });
        using var cancellation = new CancellationTokenSource();
        var runner = new Interpreter(files: new ScriptFiles());
        Task Run() => runner.RunAsync(source, _ =>
        {
            Assert.False(File.Exists(path));
            if (ending == "cancel")
            {
                cancellation.Cancel();
            }
        }, cancellation.Token);
        if (ending == "error")
        {
            await Assert.ThrowsAsync<ScriptException>(Run);
        }
        else if (ending == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        }
        else
        {
            await Run();
        }

        Assert.Equal("saved\r\n", File.ReadAllText(path));
        await runner.RunAsync($"PRINT FOPEN(\"{path}\")",
            output => Assert.Equal("1", output));
    }

    [Fact]
    public async Task AcceptanceScriptRunsThroughInterpreterAndRealFiles()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "file-io-csv-acceptance.uws"));
        List<string> output = [];
        await new Interpreter(files: new ScriptFiles()).RunAsync(
            $"fixture_path=\"{PathOf()}\"\n" + script, output.Add);
        Assert.Equal(["file-ok"], output);
    }

    [Fact]
    public async Task CloseFailuresDoNotPreventOtherPendingFilesFromSaving()
    {
        IScriptFileSession session = new ScriptFiles().CreateSession();
        string fail = PathOf("fail");
        Value bad = await session.InvokeAsync("FOPEN",
            [Value.String(fail), N(16)], default);
        Value good = await session.InvokeAsync("FOPEN",
            [Value.String(PathOf("good")), N(16)], default);
        await session.InvokeAsync("FPUT",
            [good, Value.String("ok")], default);
        Directory.CreateDirectory(fail);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            async () => await session.DisposeAsync());
        Assert.Single(error.InnerExceptions);
        Assert.Equal("ok\r\n", File.ReadAllText(PathOf("good")));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("FGET", [bad, N(1)], default));
    }

    [Fact]
    public async Task SuppressedCloseFailureAndEncodingFailurePreserveOldFile()
    {
        string path = PathOf();
        File.WriteAllText(path, "original");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(8)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("😀")], default);
        Assert.Equal(N(0), await session.InvokeAsync(
            "FCLOSE", [id, N(1)], default));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(N(0), await session.InvokeAsync(
            "FCLOSE", [id, N(1)], default));
    }

    [Fact]
    public async Task ExistenceMissingReadCapacityAndSessionIsolation()
    {
        await using IScriptFileSession first =
            new ScriptFiles().CreateSession();
        await using IScriptFileSession second =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(1), await first.InvokeAsync("FOPEN",
            [Value.String(directory + Path.DirectorySeparatorChar), N(1)],
            default));
        Assert.Equal(N(0), await first.InvokeAsync("FOPEN",
            [Value.String(directory), N(1)], default));
        Assert.Equal(N(-1), await first.InvokeAsync("FOPEN",
            [Value.String(PathOf())], default));
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(N(i + 1), await first.InvokeAsync("FOPEN",
                [Value.String(PathOf($"{i}.txt")), N(16)], default));
        }

        Assert.Equal(N(-1), await first.InvokeAsync("FOPEN",
            [Value.String(PathOf("extra")), N(16)], default));
        Assert.Equal(N(1), await second.InvokeAsync("FOPEN",
            [Value.String(PathOf("second")), N(16)], default));
        await first.InvokeAsync("FCLOSE", [N(1)], default);
        Assert.Equal(N(65), await first.InvokeAsync("FOPEN",
            [Value.String(PathOf("extra")), N(16)], default));
    }

    [Fact]
    public async Task ReadOnlyRejectsMutationAndCancelledOpenHasNoEffect()
    {
        string path = PathOf();
        File.WriteAllText(path, "read only");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("FPUT",
                [id, Value.String("changed")], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("FOPEN",
                [Value.String(PathOf("cancel")), N(16)], cancellation.Token));
        Assert.False(File.Exists(PathOf("cancel")));
        Assert.Equal("read only", File.ReadAllText(path));
    }

    [Fact]
    public async Task ConcurrentRunsKeepIdsAndBufferedContentsSeparate()
    {
        var runner = new Interpreter(files: new ScriptFiles());
        async Task Run(string filename, string value)
        {
            List<string> output = [];
            await runner.RunAsync(
                $"id=FOPEN(\"{PathOf(filename)}\", F_WRITE8)\n"
                + $"FPUT(id, \"{value}\")\nPRINT id\nSLEEP(0.01)",
                output.Add);
            Assert.Equal(["1"], output);
        }

        await Task.WhenAll(Run("first", "a"), Run("second", "b"));
        Assert.Equal("a\r\n", File.ReadAllText(PathOf("first")));
        Assert.Equal("b\r\n", File.ReadAllText(PathOf("second")));
    }

    [Fact]
    public async Task InvalidEncodingOpenFailsAndCloseErrorIsObservable()
    {
        File.WriteAllBytes(PathOf(), [0xff, 0xfe, 0x41]);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(-1), await session.InvokeAsync("FOPEN",
            [Value.String(PathOf())], default));
        string target = PathOf("blocked");
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(target), N(16)], default);
        Directory.CreateDirectory(target);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await session.InvokeAsync("FCLOSE", [id], default));
        Assert.Equal(N(0), await session.InvokeAsync(
            "FCLOSE", [id, N(1)], default));
    }
}
