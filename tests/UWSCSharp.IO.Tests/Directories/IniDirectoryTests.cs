using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class IniDirectoryTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-ini-dir-" + Guid.NewGuid());

    public IniDirectoryTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // 専用fixture内だけの読み取り専用属性を解除して削除します。
        foreach (string path in Directory.EnumerateFiles(
            directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(directory, true);
    }

    private string PathOf(string name) => Path.Combine(directory, name);

    private static Value N(int number) => Value.Numeric(number);

    [Fact]
    public async Task IniCreateUpdateDeleteAndNumericValues()
    {
        string path = PathOf("settings");
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "READINI", [Value.String("main"), Value.String("number"),
                Value.String(path)], default));
        await session.InvokeAsync("WRITEINI",
            [Value.String("Main"), Value.String("number"), N(123),
                Value.String(path)], default);
        Assert.True(File.Exists(path + ".INI"));
        Assert.Equal(N(123), await session.InvokeAsync("READINI",
            [Value.String("MAIN"), Value.String("NUMBER"),
                Value.String(path)], default));
        await session.InvokeAsync("WRITEINI",
            [Value.String("Main"), Value.String("text"), Value.String("a=b"),
                Value.String(path)], default);
        await session.InvokeAsync("WRITEINI",
            [Value.String("Other"), Value.String("keep"), Value.String("yes"),
                Value.String(path)], default);
        await session.InvokeAsync("DELETEINI",
            [Value.String("Main"), Value.String("number"),
                Value.String(path)], default);
        Assert.Equal(Value.String("a=b"), await session.InvokeAsync(
            "READINI", [Value.String("Main"), Value.String("text"),
                Value.String(path)], default));
        await session.InvokeAsync("DELETEINI",
            [Value.String("Main"), new Value(ValueKind.Omitted),
                Value.String(path)], default);
        string content = File.ReadAllText(path + ".INI");
        Assert.DoesNotContain("[Main]", content);
        Assert.Contains("[Other]", content);
        Assert.Contains("keep=yes", content);
    }

    [Theory]
    [InlineData("cp932")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task IniPreservesEncodingCommentsAndFirstDuplicate(string kind)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = kind switch
        {
            "cp932" => Encoding.GetEncoding(932),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            _ => new UnicodeEncoding(true, true)
        };
        string path = PathOf("settings.ini");
        File.WriteAllText(path,
            ";日本語\r\n[s]\r\nx=old\r\nx=second\r\n#keep\r\n[t]\r\ny=stay\r\n",
            encoding);
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        await session.InvokeAsync("WRITEINI",
            [Value.String("s"), Value.String("x"), Value.String("変更"),
                Value.String(path)], default);
        string expected = ";日本語\r\n[s]\r\nx=変更\r\nx=second\r\n#keep\r\n[t]\r\ny=stay\r\n";
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)),
            File.ReadAllBytes(path));
    }

    [Fact]
    public async Task IniUpgradesCp932AndUsesExplicitScriptContext()
    {
        string path = PathOf("task.INI");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(path, "[s]\r\nx=日本語\r\n", Encoding.GetEncoding(932));
        await using IScriptFileSession session =
            new ScriptFiles("elsewhere/task.uws", directory).CreateSession();
        await session.InvokeAsync("WRITEINI",
            [Value.String("s"), Value.String("x"), Value.String("😀")], default);
        Assert.Equal(new UTF8Encoding(false).GetBytes("[s]\r\nx=😀\r\n"),
            File.ReadAllBytes(path));
        Assert.Equal(Value.String("😀"), await session.InvokeAsync("READINI",
            [Value.String("s"), Value.String("x")], default));
        await session.InvokeAsync("DELETEINI", [Value.String("s")], default);
        Assert.Equal("", File.ReadAllText(path));
    }

    [Fact]
    public async Task IniMissingContextAndLockedFileAreReported()
    {
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("READINI",
                [Value.String("s"), Value.String("x")], default));
        string path = PathOf("locked.ini");
        File.WriteAllText(path, "[s]\nx=old");
        using (FileStream locked = File.Open(path, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () =>
                await session.InvokeAsync("WRITEINI",
                    [Value.String("s"), Value.String("x"), Value.String("new"),
                        Value.String(path)], default));
        }

        Assert.Equal("[s]\nx=old", File.ReadAllText(path));
        string missing = PathOf("missing.ini");
        await session.InvokeAsync("DELETEINI",
            [Value.String("s"), Value.String("x"), Value.String(missing)], default);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task GetDirPublishesCountArrayAndForInSnapshot()
    {
        File.WriteAllText(PathOf("f10.txt"), "ten");
        File.WriteAllText(PathOf("f2.txt"), "two");
        Directory.CreateDirectory(PathOf("sub"));
        File.WriteAllText(PathOf("sub/ignored.txt"), "nested");
        List<string> output = [];
        await new Interpreter(files: new ScriptFiles()).RunAsync($$"""
            PRINT GETDIR("{{directory}}", "*.TXT")
            PRINT JOIN(GETDIR_FILES, "|")
            FOR name IN GETDIR("{{directory}}", "*.txt")
                PRINT name
                GETDIR("{{directory}}", "missing*")
            NEXT
            PRINT LENGTH(GETDIR_FILES)
            PRINT GETDIR("{{directory}}", "\")
            PRINT GETDIR_FILES[0]
            """, output.Add);
        Assert.Equal(["2", "f2.txt|f10.txt", "f2.txt", "f10.txt", "0", "1", "sub"],
            output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GetDirOrdersByMetadata(int order)
    {
        string first = PathOf("z.txt");
        string second = PathOf("a.txt");
        File.WriteAllText(first, "x");
        File.WriteAllText(second, "longer");
        DateTime earlier = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(first, earlier);
        File.SetCreationTimeUtc(second, earlier.AddDays(1));
        File.SetLastWriteTimeUtc(first, earlier);
        File.SetLastWriteTimeUtc(second, earlier.AddDays(1));
        File.SetLastAccessTimeUtc(first, earlier);
        File.SetLastAccessTimeUtc(second, earlier.AddDays(1));
        await using IScriptFileSession session =
            new ScriptFiles().CreateSession();
        Assert.Equal(N(2), await session.InvokeAsync("GETDIR",
            [Value.String(directory), Value.String("*"), N(1), N(order)], default));
        Assert.Equal(["z.txt", "a.txt"], session.DirectoryFiles);
        Assert.Equal(N(0), await session.InvokeAsync("GETDIR",
            [Value.String(PathOf("missing"))], default));
        Assert.Empty(session.DirectoryFiles);
    }

    [Fact]
    public async Task GetDirFiltersHiddenAndKeepsSessionsSeparate()
    {
        File.WriteAllText(PathOf("visible"), "x");
        string hidden = PathOf("hidden");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        await using IScriptFileSession first = new ScriptFiles().CreateSession();
        await using IScriptFileSession second = new ScriptFiles().CreateSession();
        Assert.Equal(N(1), await first.InvokeAsync("GETDIR",
            [Value.String(directory)], default));
        Assert.Equal(["visible"], first.DirectoryFiles);
        Assert.Equal(N(2), await second.InvokeAsync("GETDIR",
            [Value.String(directory), Value.String("*.*"), N(1)], default));
        Assert.Equal(["visible"], first.DirectoryFiles);
    }

    [Fact]
    public async Task DeleteFileReportsPartialFailureAndDoesNotDeleteDirectories()
    {
        string blocked = PathOf("blocked.tmp");
        string good = PathOf("good.tmp");
        File.WriteAllText(blocked, "keep");
        File.WriteAllText(good, "delete");
        Directory.CreateDirectory(PathOf("sub.tmp"));
        File.WriteAllText(PathOf("sub.tmp/child.tmp"), "keep");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        using (FileStream locked = File.Open(blocked, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(N(0), await session.InvokeAsync("DELETEFILE",
                [Value.String(PathOf("*.tmp"))], default));
            Assert.False(File.Exists(good));
            Assert.True(File.Exists(blocked));
            Assert.Equal(N(0), await session.InvokeAsync("DELETEFILE",
                [Value.String(blocked)], default));
        }

        Assert.True(File.Exists(PathOf("sub.tmp/child.tmp")));
        Assert.Equal(N(1), await session.InvokeAsync("DELETEFILE",
            [Value.String(blocked)], default));
        Assert.Equal(N(0), await session.InvokeAsync("DELETEFILE",
            [Value.String(blocked)], default));
        Assert.Equal(N(0), await session.InvokeAsync("DELETEFILE",
            [Value.String(PathOf("sub.tmp"))], default));
    }

    [Fact]
    public async Task FdelLineUsesBufferAndRejectsReadOnlyOrMissingRows()
    {
        string path = PathOf("lines.txt");
        File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2 | 16 | 512)], default);
        await session.InvokeAsync("FDELLINE", [id, N(2)], default);
        Assert.Equal(Value.String("one\r\nthree\r\n"),
            await session.InvokeAsync("FGET", [id, N(-2)], default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("FDELLINE", [id, N(3)], default));
        await session.InvokeAsync("FCLOSE", [id], default);
        Assert.Equal("one\r\nthree\r\n", File.ReadAllText(path));
        Value read = await session.InvokeAsync("FOPEN", [Value.String(path)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("FDELLINE", [read, N(1)], default));
    }

    [Theory]
    [InlineData("WRITEINI")]
    [InlineData("DELETEINI")]
    [InlineData("GETDIR")]
    [InlineData("DELETEFILE")]
    [InlineData("FDELLINE")]
    public async Task PreCancelledOperationsDoNotChangeFiles(string operation)
    {
        string path = PathOf("keep.ini");
        File.WriteAllText(path, "[s]\r\nx=old\r\n");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Value id = await session.InvokeAsync("FOPEN",
            [Value.String(path), N(2)], default);
        Value[] args = operation switch
        {
            "WRITEINI" => [Value.String("s"), Value.String("x"),
                Value.String("new"), Value.String(path)],
            "DELETEINI" => [Value.String("s"), Value.String("x"), Value.String(path)],
            "GETDIR" => [Value.String(directory)],
            "DELETEFILE" => [Value.String(path)],
            _ => [id, N(1)]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(operation, args, cancellation.Token));
        Assert.Equal("[s]\r\nx=old\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task AcceptanceScriptUsesDefaultIniAndDeletesOnlyFixtureFile()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "ini-directory-acceptance.uws"));
        List<string> output = [];
        await new Interpreter(files: new ScriptFiles("task.uws", directory))
            .RunAsync($"fixture_dir=\"{directory}\"\n" + script, output.Add);
        Assert.Equal(["ini-dir-ok"], output);
        Assert.False(File.Exists(PathOf("data.txt")));
        Assert.Equal("", File.ReadAllText(PathOf("task.INI")));
    }
}
