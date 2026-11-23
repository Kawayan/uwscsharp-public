using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptDropResolverTests
{
    public static TheoryData<string, string[]> ArgumentCases => new()
    {
        { "", [] },
        { " \t ", [] },
        { "\"\"", [""] },
        { "\"日本語 alpha\" beta", ["日本語 alpha", "beta"] },
        { "a\tb", ["a", "b"] },
        { "a\\\\b c", ["a\\\\b", "c"] },
        { "\"a\\\"b\"", ["a\"b"] },
        { "\"unterminated", ["unterminated"] },
        { "\"\" \"日本語\"", ["", "日本語"] }
    };

    [Theory]
    [MemberData(nameof(ArgumentCases))]
    public void ArgumentsUseWindowsRulesWithoutLaunchingAnything(string text, string[] expected)
        => Assert.Equal(expected, ScriptDropResolver.SplitArguments(text));

    [Fact]
    public void EmbeddedNulIsNotSilentlyTruncated()
        => Assert.Throws<ArgumentException>(() => ScriptDropResolver.SplitArguments("a\0b"));

    [Fact]
    public void OrdinaryPathKeepsExistingLoadingAndEmptyArguments()
    {
        var resolver = new ScriptDropResolver(_ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());
        DroppedScript result = resolver.Resolve("relative.txt");
        Assert.Equal("relative.txt", result.Path);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void LinkReturnsTargetAndArgumentSnapshotWithoutChangingProcessDirectory()
    {
        string current = Environment.CurrentDirectory;
        var resolver = new ScriptDropResolver(_ =>
            new ScriptShortcut(@"C:\scripts\対象.UWS", "\"日本語 alpha\" beta"), _ => true);
        DroppedScript result = resolver.Resolve("entry.LNK");
        Assert.Equal(@"C:\scripts\対象.UWS", result.Path);
        Assert.Equal(["日本語 alpha", "beta"], result.Arguments);
        Assert.Equal(current, Environment.CurrentDirectory);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Arguments)[0] = "changed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.uws")]
    [InlineData("C:relative.uws")]
    [InlineData("https://example.invalid/script.uws")]
    [InlineData(@"C:\app.exe")]
    [InlineData(@"C:\another.lnk")]
    public void UnsupportedTargetsAreRejectedBeforeExecution(string target)
    {
        var resolver = new ScriptDropResolver(_ => new(target, ""), _ => true);
        Assert.Throws<InvalidDataException>(() => resolver.Resolve("entry.lnk"));
    }

    [Fact]
    public void MissingLinkDoesNotReadMetadata()
    {
        var resolver = new ScriptDropResolver(_ => throw new InvalidOperationException(), _ => false);
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve("missing.lnk"));
    }

    [Fact]
    public void MissingTargetIsNotTreatedAsAnEmptySuccessfulScript()
    {
        string link = Path.GetFullPath("entry.lnk");
        var resolver = new ScriptDropResolver(_ => new(@"C:\missing.uws", ""), path => path == link);
        Assert.Throws<FileNotFoundException>(() => resolver.Resolve(link));
    }

    [Fact]
    public void MetadataFailureIsPreserved()
    {
        var expected = new IOException("リンクの読取り失敗");
        var resolver = new ScriptDropResolver(_ => throw expected, _ => true);
        Assert.Same(expected, Assert.Throws<IOException>(() => resolver.Resolve("entry.lnk")));
    }
}
