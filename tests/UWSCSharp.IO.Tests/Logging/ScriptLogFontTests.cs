using System.Globalization;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class ScriptLogFontTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-font-" + Guid.NewGuid());

    public ScriptLogFontTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,12", "Consolas", 12)]
    [InlineData(" ＭＳ ゴシック , 14.5 ", "ＭＳ ゴシック", 14.5)]
    [InlineData("Consolas,0", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,-1", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,NaN", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,Infinity", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,1e309", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,twelve", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas", "ＭＳ ゴシック", 10)]
    [InlineData(",12", "ＭＳ ゴシック", 10)]
    [InlineData("Consolas,12,5", "ＭＳ ゴシック", 10)]
    [InlineData("file:///fonts/#Example,12", "ＭＳ ゴシック", 10)]
    [InlineData("https://example.invalid/#Example,12", "ＭＳ ゴシック", 10)]
    [InlineData("Example\tFont,12", "ＭＳ ゴシック", 10)]
    public async Task FontAndLimitComeFromSameIniDocument(
        string text, string family, double size)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string content = "[SET]\nLogLines=3\nLogFont=" + text;
        await File.WriteAllTextAsync(path, content);
        ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(path);
        Assert.Equal(3, defaults.MaximumLines);
        Assert.Equal(family, defaults.Font.Family);
        Assert.Equal(size, defaults.Font.SizePoints);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FontSnapshotDoesNotFollowSubsequentEdits()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path, "[SET]\nLogFont=Consolas,12");
        ScriptLogDefaults first = await ScriptLogDefaults.LoadAsync(path);
        await File.WriteAllTextAsync(path, "[SET]\nLogFont=Arial,16");
        ScriptLogDefaults second = await ScriptLogDefaults.LoadAsync(path);
        Assert.Equal("Consolas", first.Font.Family);
        Assert.Equal(12, first.Font.SizePoints);
        Assert.Equal("Arial", second.Font.Family);
        Assert.Equal(16, second.Font.SizePoints);
    }

    [Fact]
    public async Task IniFontSizeDoesNotDependOnCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string path = Path.Combine(directory, "UWSC.INI");
            await File.WriteAllTextAsync(path, "[SET]\nLogFont=Consolas,12.5");
            ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(path);
            Assert.Equal(12.5, defaults.Font.SizePoints);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
