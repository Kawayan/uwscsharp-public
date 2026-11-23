using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptStartupDefaultsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-startup-defaults-" + Guid.NewGuid());

    public ScriptStartupDefaultsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Arial,18", "Arial,18")]
    [InlineData("Consolas,12", "Consolas,12")]
    [InlineData("Arial", "Arial,9")]
    [InlineData("Arial,9.5", "Arial,9")]
    [InlineData("Consolas,12.9", "Consolas,12")]
    [InlineData("Arial,bad", "Arial,9")]
    [InlineData("Consolas,0", "Consolas,9")]
    [InlineData("Consolas,-1", "Consolas,9")]
    [InlineData("Arial,0.5", "Arial,9")]
    [InlineData(" ,12", "Tahoma,12")]
    [InlineData("Consolas,12,13", "Consolas,12")]
    [InlineData("Arial,NaN", "Arial,9")]
    [InlineData("Arial,1e308", "Arial,9")]
    public async Task ParsesTheIniFontWithoutChangingRuntimeOptionRules(
        string? font, string? expected)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "[SET]\nLogLines=3\nLogFont=Consolas,12\n"
            + (font is null ? "" : "DefaultFont=" + font + "\n");
        await File.WriteAllTextAsync(path, document);
        ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(path);
        Assert.Equal(expected, startup.DefaultFont);
        Assert.Equal(3, startup.Log.MaximumLines);
        Assert.Equal("Consolas", startup.Log.Font.Family);
        Assert.Equal(12, startup.Log.Font.SizePoints);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DuplicateKeysUseTheFirstValueAndSnapshotsStayIndependent()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path, "[SET]\nDefaultFont=Arial,18\nDefaultFont=Tahoma,10\n");
        ScriptStartupDefaults first = await ScriptStartupDefaults.LoadAsync(path);
        await File.WriteAllTextAsync(path, "[SET]\nDefaultFont=Consolas,12\nLogLines=2\n");
        ScriptStartupDefaults second = await ScriptStartupDefaults.LoadAsync(path);
        Assert.Equal("Arial,18", first.DefaultFont);
        Assert.Equal(2000, first.Log.MaximumLines);
        Assert.Equal("Consolas,12", second.DefaultFont);
        Assert.Equal(2, second.Log.MaximumLines);
    }

    [Fact]
    public async Task MissingFileCancellationAndReadFailureAreHandledBeforeExecution()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        Assert.Null((await ScriptStartupDefaults.LoadAsync(path)).DefaultFont);
        Assert.False(File.Exists(path));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptStartupDefaults.LoadAsync(path, stop.Token).AsTask());
        Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            ScriptStartupDefaults.LoadAsync(path).AsTask());
    }
}
