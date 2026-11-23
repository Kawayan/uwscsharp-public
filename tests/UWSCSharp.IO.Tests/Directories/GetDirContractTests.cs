using System.Globalization;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class GetDirContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "UWSCSharp-getdir-" + Guid.NewGuid());

    public GetDirContractTests()
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

    private string PathOf(string name) => Path.Combine(directory, name);

    private static Value S(string text) => Value.String(text);


    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "x\0")]
    [InlineData(1, "x\0")]
    [InlineData(1, "sub/*.txt")]
    [InlineData(1, "sub\\*.txt")]
    [InlineData(1, "x:y")]
    [InlineData(1, "x\ny")]
    [InlineData(1, "x|y")]
    [InlineData(2, "$1")]
    [InlineData(2, "NaN")]
    [InlineData(3, "$1")]
    [InlineData(3, "5")]
    [InlineData(3, "-1")]
    public async Task InvalidArgumentsPreserveThePreviousSnapshot(int position, string value)
    {
        File.WriteAllText(PathOf("keep.txt"), "keep");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("GETDIR", [S(directory)], default);
        IReadOnlyList<string> previous = session.DirectoryFiles;
        Value[] args = [S(directory), S("*"), Value.Empty, Value.Empty];
        args[position] = S(value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("GETDIR", args, default).AsTask());
        Assert.Same(previous, session.DirectoryFiles);
        Assert.Equal(["keep.txt"], previous);
    }

    [Theory]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.HashTable)]
    [InlineData(ValueKind.Object)]
    public void InvalidTypesAndNonfiniteNumbersAreRejectedInEveryPosition(ValueKind kind)
    {
        for (int i = 0; i < 4; i++)
        {
            Value[] args = [S(directory), S("*"), Value.Empty, Value.Empty];
            args[i] = new Value(kind);
            Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("GETDIR", args));
            foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                args[i] = new Value(ValueKind.Number, number);
                Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate("GETDIR", args));
            }
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ManagedNaturalNamesAndOrdinalTiesAreCultureIndependent(string culture)
    {
        string[] names = ["f10.txt", "f2.txt", "f01.txt", "f1.txt", "A3.txt", "a3.txtx", "日本語10.txt", "日本語2.txt", "😀.txt"];
        foreach (string name in names)
        {
            File.WriteAllText(PathOf(name), "same");
        }

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using IScriptFileSession session = new ScriptFiles().CreateSession();
            await session.InvokeAsync("GETDIR", [S(directory)], default);
            Assert.Equal(["A3.txt", "a3.txtx", "f01.txt", "f1.txt", "f2.txt", "f10.txt", "日本語2.txt", "日本語10.txt", "😀.txt"], session.DirectoryFiles);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)session.DirectoryFiles)[0] = "changed");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task PatternsDirectoryModeHiddenSystemAndOldSnapshotsHaveDefinedEffects()
    {
        File.WriteAllText(PathOf("f2.txt"), "two");
        File.WriteAllText(PathOf("f10.txt"), "ten");
        File.WriteAllText(PathOf("noext"), "plain");
        File.WriteAllText(PathOf("hidden.txt"), "hidden");
        File.WriteAllText(PathOf("system.txt"), "system");
        File.SetAttributes(PathOf("hidden.txt"), FileAttributes.Hidden);
        File.SetAttributes(PathOf("system.txt"), FileAttributes.System);
        Directory.CreateDirectory(PathOf("dir2"));
        Directory.CreateDirectory(PathOf("hidden-dir"));
        File.SetAttributes(PathOf("hidden-dir"), FileAttributes.Hidden);
        File.WriteAllText(PathOf("dir2/nested.txt"), "nested");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Value.Numeric(4), await session.InvokeAsync("GETDIR", [S(directory)], default));
        IReadOnlyList<string> previous = session.DirectoryFiles;
        Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETDIR", [S(directory), S("*.txt")], default));
        Assert.DoesNotContain("nested.txt", session.DirectoryFiles);
        Assert.Contains("system.txt", session.DirectoryFiles);
        Assert.Equal(Value.Numeric(5), await session.InvokeAsync("GETDIR", [S(directory), S("*.*"), Value.Numeric(1e20)], default));
        Assert.Contains("noext", session.DirectoryFiles);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("GETDIR", [S(directory), S("\\")], default));
        Assert.Equal(["dir2"], session.DirectoryFiles);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETDIR", [S(directory), S("\\"), Value.Numeric(-1)], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETDIR", [S(directory), Value.Empty], default));
        Assert.Empty(session.DirectoryFiles);
        Assert.Equal(4, previous.Count);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETDIR", [S(PathOf("missing"))], default));
        Assert.Empty(session.DirectoryFiles);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETDIR", [S(PathOf("noext"))], default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task MetadataOrdersAreAscendingAndUseNaturalNameTies(int order)
    {
        DateTime baseline = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] names = ["f10.txt", "f2.txt", "f1.txt"];
        for (int i = 0; i < names.Length; i++)
        {
            string path = PathOf(names[i]);
            File.WriteAllText(path, new string('x', i == 2 ? 1 : 2));
            File.SetCreationTimeUtc(path, baseline.AddMinutes(i == 2 ? 0 : 1));
            File.SetLastWriteTimeUtc(path, baseline.AddMinutes(i == 2 ? 0 : 1));
            File.SetLastAccessTimeUtc(path, baseline.AddMinutes(i == 2 ? 0 : 1));
        }

        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("GETDIR", [S(directory), S("*"), Value.Empty, Value.Numeric(order)], default);
        Assert.Equal(["f1.txt", "f2.txt", "f10.txt"], session.DirectoryFiles);
    }

    [Fact]
    public async Task CancellationDuringEnumerationPreservesThePreviousSnapshot()
    {
        File.WriteAllText(PathOf("keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource();
        int visited = 0;
        bool cancelDuringEnumeration = false;
        bool enumeratorDisposed = false;

        IEnumerable<FileSystemInfo> Enumerate(
            string path, string pattern, EnumerationOptions options)
        {
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(path)
                    .EnumerateFileSystemInfos(pattern, options))
                {
                    if (cancelDuringEnumeration && ++visited == 2)
                    {
                        cancellation.Cancel();
                    }

                    yield return entry;
                }
            }
            finally
            {
                enumeratorDisposed = true;
            }
        }

        await using IScriptFileSession session = new ScriptFiles(Enumerate).CreateSession();
        await session.InvokeAsync("GETDIR", [S(directory)], default);
        IReadOnlyList<string> previous = session.DirectoryFiles;
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(PathOf("item" + i + ".txt"), "");
        }

        cancelDuringEnumeration = true;
        enumeratorDisposed = false;
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.InvokeAsync("GETDIR", [S(directory)], cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, visited);
        Assert.True(enumeratorDisposed);
        Assert.Same(previous, session.DirectoryFiles);
        Assert.Equal(["keep.txt"], previous);
        cancelDuringEnumeration = false;
        Assert.Equal(Value.Numeric(4), await session.InvokeAsync("GETDIR", [S(directory)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnumerationFailureDisposesIteratorAndPrioritizesCancellation(bool cancel)
    {
        File.WriteAllText(PathOf("keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource();
        bool fail = false;
        bool disposed = false;
        int visited = 0;

        IEnumerable<FileSystemInfo> Enumerate(
            string path, string pattern, EnumerationOptions options)
        {
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(path)
                    .EnumerateFileSystemInfos(pattern, options))
                {
                    visited++;
                    yield return entry;
                    if (fail)
                    {
                        if (cancel)
                        {
                            cancellation.Cancel();
                        }

                        throw new IOException("列挙途中の失敗");
                    }
                }
            }
            finally
            {
                disposed = true;
            }
        }

        await using IScriptFileSession session = new ScriptFiles(Enumerate).CreateSession();
        await session.InvokeAsync("GETDIR", [S(directory)], default);
        IReadOnlyList<string> previous = session.DirectoryFiles;
        fail = true;
        disposed = false;
        visited = 0;
        if (cancel)
        {
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.InvokeAsync("GETDIR", [S(directory)], cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Same(previous, session.DirectoryFiles);
        }
        else
        {
            Assert.Equal(Value.Numeric(0),
                await session.InvokeAsync("GETDIR", [S(directory)], cancellation.Token));
            Assert.Empty(session.DirectoryFiles);
        }

        Assert.Equal(1, visited);
        Assert.True(disposed);
        Assert.Equal(["keep.txt"], previous);
    }

    [Fact]
    public async Task LargeEnumerationPublishesCompleteSnapshotWithoutChangingPrevious()
    {
        File.WriteAllText(PathOf("keep.txt"), "keep");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        await session.InvokeAsync("GETDIR", [S(directory)], default);
        IReadOnlyList<string> previous = session.DirectoryFiles;
        var expected = new HashSet<string>(StringComparer.Ordinal) { "keep.txt" };
        for (int i = 0; i < 6000; i++)
        {
            string name = "item" + i + ".txt";
            File.WriteAllText(PathOf(name), "");
            expected.Add(name);
        }

        Assert.Equal(Value.Numeric(6001),
            await session.InvokeAsync("GETDIR", [S(directory)], default));
        Assert.Equal(6001, session.DirectoryFiles.Count);
        Assert.True(expected.SetEquals(session.DirectoryFiles));
        Assert.NotSame(previous, session.DirectoryFiles);
        Assert.Equal(["keep.txt"], previous);
    }

    [Fact]
    public async Task DirectParenthesizedAndExplicitArrayLoopsKeepTheirSnapshot()
    {
        File.WriteAllText(PathOf("f2.txt"), "two");
        File.WriteAllText(PathOf("f10.txt"), "ten");
        foreach (string expression in new[] { "GETDIR(PATH)", "(GETDIR(PATH))", "GETDIR_FILES" })
        {
            var output = new List<string>();
            string script = "GETDIR(PATH)\nFOR name IN " + expression + "\nPRINT name\nGETDIR(PATH,\"missing*\")\nNEXT\nPRINT LENGTH(GETDIR_FILES)";
            await new Interpreter(files: new ScriptFiles()).RunAsync(script.Replace("PATH", "\"" + directory + "\""), output.Add);
            Assert.Equal(["f2.txt", "f10.txt", "0"], output);
        }
    }
}
