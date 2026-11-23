using System.Globalization;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class DeleteFileContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-deletefile-" + Guid.NewGuid());

    public DeleteFileContractTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (!new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        Directory.Delete(directory, true);
    }

    private string PathOf(string name) => Path.Combine(directory, name);

    private static Value S(string text) => Value.String(text);

    [Theory]
    [InlineData("")]
    [InlineData("x\0")]
    [InlineData("x\n")]
    [InlineData("x|y")]
    [InlineData("x<y")]
    [InlineData("x\"y")]
    [InlineData("dir*/x.txt")]
    [InlineData("dir?/x.txt")]
    [InlineData("x.txt:stream")]
    public async Task InvalidPathsDoNotAffectOwnedFiles(string invalid)
    {
        string sentinel = PathOf("keep.txt");
        File.WriteAllText(sentinel, "keep");
        string path = invalid.Length == 0 ? "" : PathOf(invalid);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("DELETEFILE", [S(path)], default).AsTask());
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void InvalidTypesAndNonfiniteNumbersAreRejected(ValueKind kind)
    {
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("DELETEFILE", [new Value(kind)]));
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("DELETEFILE", [new(ValueKind.Number, number)]));
        }
    }

    [Fact]
    public async Task WildcardsDeleteOnlyTopLevelFilesIncludingHiddenAndSystem()
    {
        foreach (string name in new[] { "a.TXT", "b.txt", "hidden.txt", "system.txt", "noext" })
        {
            File.WriteAllText(PathOf(name), "owned");
        }

        File.SetAttributes(PathOf("hidden.txt"), FileAttributes.Hidden);
        File.SetAttributes(PathOf("system.txt"), FileAttributes.System);
        Directory.CreateDirectory(PathOf("sub.txt"));
        File.WriteAllText(PathOf("sub.txt/child.txt"), "keep");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.txt"))], default));
        Assert.Equal([PathOf("noext")], Directory.GetFiles(directory));
        Assert.Equal("keep", File.ReadAllText(PathOf("sub.txt/child.txt")));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.*"))], default));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.True(Directory.Exists(PathOf("sub.txt")));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("DELETEFILE", [S(PathOf("*"))], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("DELETEFILE", [S(PathOf("sub.txt"))], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("DELETEFILE", [S(PathOf("absent/file"))], default));
    }

    [Fact]
    public async Task PartialFailureIsFalseAndOtherCandidatesStillComplete()
    {
        File.WriteAllText(PathOf("a.tmp"), "keep");
        File.WriteAllText(PathOf("b.tmp"), "delete");
        File.WriteAllText(PathOf("c.tmp"), "keep-readonly");
        File.SetAttributes(PathOf("c.tmp"), FileAttributes.ReadOnly);
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        using (var lease = new FileStream(PathOf("a.tmp"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.tmp"))], default));
            Assert.False(File.Exists(PathOf("b.tmp")));
            Assert.True(File.Exists(PathOf("a.tmp")));
            Assert.True(File.Exists(PathOf("c.tmp")));
        }

        File.SetAttributes(PathOf("c.tmp"), FileAttributes.Normal);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.tmp"))], default));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.tmp"))], default));
    }

    [Fact]
    public async Task FileLinksAndDanglingLinksAreRemovedWithoutDeletingTargets()
    {
        File.WriteAllText(PathOf("target.bin"), "target");
        File.CreateSymbolicLink(PathOf("link.tmp"), PathOf("target.bin"));
        File.CreateSymbolicLink(PathOf("broken.tmp"), PathOf("absent.bin"));
        Directory.CreateDirectory(PathOf("target-dir"));
        File.WriteAllText(PathOf("target-dir/keep.txt"), "keep");
        Directory.CreateSymbolicLink(PathOf("dir.tmp"), PathOf("target-dir"));
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("DELETEFILE", [S(PathOf("*.tmp"))], default));
        Assert.DoesNotContain(PathOf("link.tmp"), Directory.GetFileSystemEntries(directory));
        Assert.DoesNotContain(PathOf("broken.tmp"), Directory.GetFileSystemEntries(directory));
        Assert.True(Directory.Exists(PathOf("dir.tmp")));
        Assert.Equal("target", File.ReadAllText(PathOf("target.bin")));
        Assert.Equal("keep", File.ReadAllText(PathOf("target-dir/keep.txt")));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task UnicodeAndLiteralNamesAreCultureIndependent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            foreach (string name in new[] { "日本語😀.txt", "[literal].txt", "semi;hash#.txt", "1.5" })
            {
                File.WriteAllText(PathOf(name), "owned");
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("DELETEFILE", [S(PathOf(name))], default));
                Assert.False(File.Exists(PathOf(name)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task CancellationAfterDeletionStopsLaterFilesAndDoesNotRollBack()
    {
        string[] candidates = [PathOf("item1.tmp"), PathOf("item2.tmp"),
            PathOf("item3.tmp")];
        foreach (string path in candidates)
        {
            File.WriteAllText(path, "owned");
        }

        File.WriteAllText(PathOf("keep.bin"), "sentinel");
        using var cancellation = new CancellationTokenSource();
        var deleted = new List<string>();
        var files = new ScriptFiles(path =>
        {
            File.Delete(path);
            deleted.Add(path);
            cancellation.Cancel();
        });
        Value[] arguments = [S(PathOf("item*.tmp"))];
        await using IScriptFileSession session = files.CreateSession();
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.InvokeAsync("DELETEFILE",
                    arguments, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        string first = Assert.Single(deleted);
        Assert.Contains(first, candidates);
        Assert.False(File.Exists(first));
        Assert.All(candidates.Where(path => path != first),
            path => Assert.Equal("owned", File.ReadAllText(path)));

        await using IScriptFileSession retry = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Numeric(1), await retry.InvokeAsync(
            "DELETEFILE", arguments, default));
        Assert.Empty(Directory.GetFiles(directory, "item*.tmp"));
        Assert.Equal("sentinel", File.ReadAllText(PathOf("keep.bin")));
    }
}
