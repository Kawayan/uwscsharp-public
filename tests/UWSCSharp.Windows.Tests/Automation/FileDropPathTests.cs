using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class FileDropPathTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-drop-path-" + Guid.NewGuid().ToString("N"));

    public FileDropPathTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "group", "nested"));
        File.WriteAllText(Path.Combine(directory, "group", "one.txt"), "one");
        File.WriteAllText(Path.Combine(directory, "group", "日本語😀 space.txt"), "two");
    }

    [Fact]
    public void ResolvesRelativeDirectoryAndNamesInOrder()
    {
        IReadOnlyList<string> paths = FileDropPaths.Resolve("group",
            ["nested\\..\\one.txt", "日本語😀 space.txt", "nested"], directory);
        Assert.Equal(
        [
            Path.Combine(directory, "group", "one.txt"),
            Path.Combine(directory, "group", "日本語😀 space.txt"),
            Path.Combine(directory, "group", "nested")
        ], paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("before\0after")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("C:drive-relative.txt")]
    [InlineData("\\root-relative.txt")]
    public void RejectsInvalidOrRootedNames(string name)
        => Assert.Throws<ArgumentException>(() =>
            FileDropPaths.Resolve("group", [name], directory));

    [Fact]
    public void ReportsMissingFileAndNonDirectoryBase()
    {
        Assert.Throws<FileNotFoundException>(() =>
            FileDropPaths.Resolve("group", ["missing.txt"], directory));
        Assert.Throws<ArgumentException>(() =>
            FileDropPaths.Resolve("group\\one.txt", ["one.txt"], directory));
        Assert.Throws<ArgumentException>(() =>
            FileDropPaths.Resolve("group", [], directory));
    }

    [Fact]
    public void CancellationPrecedesFileSystemAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FileDropPaths.Resolve("missing", ["one.txt"], directory, cancellation.Token));
    }

    public void Dispose()
    {
        string root = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(directory, recursive: true);
    }
}
