using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class CsvExclusiveTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-exclusive-" + Guid.NewGuid());

    public CsvExclusiveTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このfixture専用の一意なディレクトリだけを削除します。
        Directory.Delete(directory, true);
    }

    private string PathOf(string name = "data.csv") =>
        Path.Combine(directory, name);

    private static Value N(int number) => Value.Numeric(number);

    [Theory]
    [InlineData("a,b,c", 2, "X", 0, "a,X,c")]
    [InlineData("a,\"b,c\",d", 2, "X", 0, "a,X,d")]
    [InlineData("a,\"b,c\",d", 2, "X", 3, "a,X,c\",d")]
    [InlineData("a,b", 4, "X", 0, "a,b,,X")]
    [InlineData("", 3, "X", 0, ",,X")]
    [InlineData(",a,b", 1, "X", 0, "X,a,b")]
    [InlineData("a,b,", 2, "X", 0, "a,X")]
    [InlineData("a,b,,", 2, "X", 0, "a,X,,")]
    [InlineData("a,b,   ", 2, "X", 0, "a,X")]
    [InlineData("x,y", 1, "a,b", 2, "a,b,y")]
    [InlineData("x,y", 1, "a\"b", 2, "\"a\"\"b\",y")]
    [InlineData("x,y", 1, "a\"b", 0, "a\"b,y")]
    [InlineData("x,y", 1, "a\"b", 1, "a\"b,y")]
    public async Task ColumnReplacementPreservesStaticModeDifferences(
        string line, int column, string value, int mode, string expected)
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(PathOf()), N(16)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String(line)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String(value), N(1), N(column), N(mode)], default);
        Assert.Equal(Value.String(expected + "\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(expected + "\r\n", File.ReadAllText(PathOf()));
    }

    [Fact]
    public async Task TabColumnsPaddingAndReadAfterWriteUseFreshContents()
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(PathOf()), N(16 | 256)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("first\told\tlast")], default);
        Assert.Equal(Value.String("old"), await session.InvokeAsync(
            "FGET", [id, N(1), N(2)], default));
        await session.InvokeAsync("FPUT",
            [id, Value.String("new"), N(1), N(2)], default);
        Assert.Equal(Value.String("first"), await session.InvokeAsync(
            "FGET", [id, N(1), N(1)], default));
        Assert.Equal(Value.String("new"), await session.InvokeAsync(
            "FGET", [id, N(1), N(2)], default));
        await session.InvokeAsync("FPUT",
            [id, Value.String("x\"y"), N(3), N(3), N(2)], default);
        Assert.Equal(Value.String(
            "first\tnew\tlast\r\n\r\n\t\t\"x\"\"y\"\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-2, 0)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task NonColumnWritesBypassQuoteEncoding(int row, int column)
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(PathOf()), N(16)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("a\"b"), N(row), N(column), N(2)], default);
        Assert.Equal(Value.String("a\"b\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
    }

    [Theory]
    [InlineData(2 | 512)]
    [InlineData(16 | 512)]
    [InlineData(2 | 16 | 512)]
    public async Task ExclusiveLeaseBlocksOtherOpensAndRespectsReadMode(
        int mode)
    {
        string path = PathOf();
        File.WriteAllText(path, "existing\r\n");
        await using IScriptFileSession owner =
            new ScriptFiles().CreateSession();
        await using IScriptFileSession other =
            new ScriptFiles().CreateSession();
        Value id = await owner.InvokeAsync("FOPEN",
            [Value.String(path), N(mode)], default);
        Assert.Equal(N(1), id);
        string expected = (mode & 2) != 0 ? "existing" : "";
        Assert.Equal(Value.String(expected), await owner.InvokeAsync(
            "FGET", [id, N(1)], default));
        Assert.ThrowsAny<IOException>(() => File.OpenRead(path));
        Assert.ThrowsAny<IOException>(() => File.OpenWrite(path));
        Assert.Equal(N(-1), await other.InvokeAsync("FOPEN",
            [Value.String(Path.Combine(directory, ".", "data.csv")), N(2)],
            default));
        Assert.Equal(N(-1), await other.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 16 | 512)], default));
        Assert.Equal(N(-1), await other.InvokeAsync("FOPEN",
            [Value.String(path), N(16)], default));
        Assert.Equal(N(-1), await owner.InvokeAsync("FOPEN",
            [Value.String(path), N(16)], default));
        await owner.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal(expected.Length == 0 ? "" : "existing\r\n", File.ReadAllText(path));
        Assert.Equal(N(1), await other.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 512)], default));
    }

    [Fact]
    public async Task MissingExclusiveWritableCreatesFileAndReadOnlyFails()
    {
        string path = PathOf();
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(-1), await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 512)], default));
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(16 | 512)], default);
        Assert.Equal(N(1), id);
        Assert.True(File.Exists(path));
        await session.InvokeAsync("FPUT",
            [id, Value.String("saved")], default);
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("saved\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("exit")]
    [InlineData("exitexit")]
    [InlineData("error")]
    [InlineData("cancel")]
    public async Task InterpreterSavesAndReleasesLeaseOnEveryExit(string ending)
    {
        string path = PathOf();
        File.WriteAllText(path, "old");
        string tail = ending switch
        {
            "exit" => "EXIT",
            "exitexit" => "EXITEXIT",
            "error" => "PRINT 1/0",
            "cancel" => "PRINT \"unexpected\"",
            _ => ""
        };
        string source = $"id=FOPEN(\"{path}\", F_WRITE8 OR F_EXCLUSIVE)\n"
            + "FPUT(id, \"new\", 1, 1)\nPRINT \"held\"\n" + tail;
        using var cancellation = new CancellationTokenSource();
        Task Run() => new Interpreter(files: new ScriptFiles()).RunAsync(
            source, text =>
            {
                Assert.Equal("held", text);
                Assert.ThrowsAny<IOException>(() => File.OpenRead(path));
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

        Assert.Equal("new\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodingFailureReleasesLeaseAndPreservesOldBytes(
        bool suppress)
    {
        string path = PathOf();
        File.WriteAllText(path, "old");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(8 | 512)], default);
        await session.InvokeAsync("FPUT",
            [id, Value.String("😀"), N(1)], default);
        if (suppress)
        {
            Assert.Equal(N(0), await session.InvokeAsync("FCLOSE",
                [id, N(1)], default));
        }
        else
        {
            await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(
                async () => await session.InvokeAsync("FCLOSE", [id], default));
        }

        Assert.Equal("old", File.ReadAllText(path));
        using FileStream reopened = File.Open(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task DecodeFailureReleasesAcquiredLease()
    {
        string path = PathOf();
        File.WriteAllBytes(path, [0xff, 0xfe, 0x41]);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(-1), await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 512)], default));
        using FileStream reopened = File.Open(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task DisposalContinuesSavingAndReleasesAllExclusiveLeases()
    {
        string first = PathOf("first");
        string second = PathOf("second");
        File.WriteAllText(first, "old");
        File.WriteAllText(second, "old");
        IScriptFileSession session = new ScriptFiles().CreateSession();
        Value bad = await session.InvokeAsync("FOPEN",
            [Value.String(first), N(8 | 512)], default);
        Value good = await session.InvokeAsync("FOPEN",
            [Value.String(second), N(16 | 512)], default);
        await session.InvokeAsync("FPUT",
            [bad, Value.String("😀"), N(1)], default);
        await session.InvokeAsync("FPUT",
            [good, Value.String("saved"), N(1)], default);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            async () => await session.DisposeAsync());
        Assert.IsType<System.Text.EncoderFallbackException>(
            Assert.Single(error.InnerExceptions));
        Assert.Equal("old", File.ReadAllText(first));
        Assert.Equal("saved\r\n", File.ReadAllText(second));
        using FileStream firstLease = File.Open(first, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using FileStream secondLease = File.Open(second, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        await session.DisposeAsync();
    }
}
