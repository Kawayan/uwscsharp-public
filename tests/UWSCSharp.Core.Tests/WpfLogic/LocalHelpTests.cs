using System.ComponentModel;
using System.Text;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LocalHelpTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSC help 日本語 # " + Guid.NewGuid().ToString("N"));

    private static Dictionary<string, byte[]> Files() => new()
    {
        ["index.html"] = Encoding.UTF8.GetBytes("<title>日本語</title>"),
        ["assets/help.css"] = [1],
        ["assets/help.js"] = [2],
        ["assets/search-index.js"] = [3]
    };

    [Fact]
    public void EmbeddedBundleContainsPagesAndOfflineAssetsOnly()
    {
        IReadOnlyDictionary<string, byte[]> files = LocalHelp.EmbeddedFiles();
        Assert.Equal(27, files.Count);
        Assert.Equal(23, files.Keys.Count(name =>
            name.StartsWith("pages/", StringComparison.Ordinal)));
        Assert.Contains("validate_script",
            Encoding.UTF8.GetString(files["pages/authoring.html"]));
        string index = Encoding.UTF8.GetString(files["index.html"]);
        Assert.Contains("UWSC#", index);
        Assert.Contains("assets/search-index.js", index);
        Assert.Contains("pages/authoring.html", index);
        Uri extracted = new LocalHelp(directory, _ => { }).Materialize();
        var authoring = new Uri(extracted, "pages/authoring.html");
        Assert.Equal(files["pages/authoring.html"],
            File.ReadAllBytes(authoring.LocalPath));
        Assert.DoesNotContain(files.Keys, name =>
            name.Contains("content/") || name.EndsWith(".json")
                || name.EndsWith(".md"));
    }

    [Fact]
    public void ExtractionRepairsFilesBeforePassingEscapedFileUri()
    {
        Uri? opened = null;
        Dictionary<string, byte[]> source = Files();
        var help = new LocalHelp(directory, uri => opened = uri, source);
        help.Open();
        Uri first = Assert.IsType<Uri>(opened);
        Assert.True(first.IsFile);
        Assert.Contains("%23", first.AbsoluteUri);
        Assert.Equal(source["index.html"], File.ReadAllBytes(first.LocalPath));
        File.WriteAllText(first.LocalPath, "破損したキャッシュ");
        File.Delete(Path.Combine(Path.GetDirectoryName(first.LocalPath)!,
            "assets", "help.js"));
        help.Open();
        Assert.Equal(first, opened);
        Assert.Equal(source["index.html"], File.ReadAllBytes(first.LocalPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(first.LocalPath)!,
            "assets", "help.js")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ResourceSnapshotAndVersionAreIndependentOfInputOrder()
    {
        Dictionary<string, byte[]> files = Files();
        var first = new LocalHelp(directory, _ => { }, files);
        files["index.html"][0] = 0;
        var second = new LocalHelp(directory, _ => { }, files);
        Uri old = first.Materialize();
        Uri changed = second.Materialize();
        Assert.NotEqual(old, changed);
        Assert.Equal((byte)'<', File.ReadAllBytes(old.LocalPath)[0]);
        var reversed = files.Reverse()
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var reordered = new LocalHelp(directory, _ => { }, reversed);
        Assert.Equal(changed, reordered.Materialize());
    }

    [Fact]
    public void WriteFailureDoesNotLaunchBrowserAndCanBeRetried()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        File.WriteAllText(directory, "保存先がファイル");
        int calls = 0;
        var help = new LocalHelp(directory, _ => calls++, Files());
        Assert.Throws<IOException>(help.Open);
        Assert.Equal(0, calls);
        File.Delete(directory);
        help.Open();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BrowserFailurePreservesExtractedFiles()
    {
        var failure = new Win32Exception("ブラウザーの起動失敗");
        var help = new LocalHelp(directory, _ => throw failure, Files());
        Assert.Same(failure, Assert.Throws<Win32Exception>(help.Open));
        Assert.True(File.Exists(help.Materialize().LocalPath));
    }

    [Theory]
    [InlineData("../escape.html")]
    [InlineData("assets/../../escape.html")]
    [InlineData("C:/escape.html")]
    [InlineData("nested/page.html")]
    [InlineData("pages/../escape.html")]
    [InlineData("pages/nested/page.html")]
    [InlineData("editor.html")]
    [InlineData("index.html:stream")]
    public void InvalidResourceNamesAreRejectedBeforeWriting(string name)
    {
        Dictionary<string, byte[]> files = Files();
        files.Add(name, []);
        Assert.Throws<InvalidDataException>(() =>
            new LocalHelp(directory, _ => { }, files));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MissingAssetsAreRejectedBeforeWriting()
    {
        Dictionary<string, byte[]> files = Files();
        files.Remove("assets/search-index.js");
        Assert.Throws<InvalidDataException>(() =>
            new LocalHelp(directory, _ => { }, files));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ConcurrentStartsUseCompleteSharedFiles()
    {
        var help = new LocalHelp(directory, _ => { }, Files());
        Uri[] addresses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(help.Materialize)));
        Assert.All(addresses, address => Assert.Equal(addresses[0], address));
        Assert.Equal(Files()["index.html"],
            File.ReadAllBytes(addresses[0].LocalPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp",
            SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        if (File.Exists(directory))
        {
            File.Delete(directory);
        }
    }
}
