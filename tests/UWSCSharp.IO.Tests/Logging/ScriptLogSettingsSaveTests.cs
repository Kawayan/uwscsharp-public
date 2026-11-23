using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptLogSettingsSaveTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-log-settings-" + Guid.NewGuid());

    public ScriptLogSettingsSaveTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task SavePreservesEncodingCommentsAndOtherSettings(string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding selected = encoding switch
        {
            "utf16" => new UnicodeEncoding(false, true),
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        string original = "; 日本語\r\n[other]\r\nLogLines=99\r\n"
            + "[ set ]\r\n loglines = 1\r\nLogLines=7\r\n"
            + "LogFont=Arial,8\r\nStayOnTop=1\r\n[SET]\r\nLogFont=Arial,9";
        await File.WriteAllTextAsync(path, original, selected);
        var settings = new ScriptLogDefaults(123, new ScriptLogFont("Consolas", 12.5));
        await settings.SaveAsync(path);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        byte[] preamble = selected.GetPreamble();
        Assert.True(bytes.AsSpan().StartsWith(preamble));
        Assert.Equal(original.Replace(" loglines = 1", "LogLines=123")
            .Replace("LogFont=Arial,8", "LogFont=Consolas,12.5"),
            selected.GetString(bytes.AsSpan(preamble.Length)));
        ScriptLogDefaults loaded = await ScriptLogDefaults.LoadAsync(path);
        Assert.Equal(123, loaded.MaximumLines);
        Assert.Equal("Consolas", loaded.Font.Family);
        Assert.Equal(12.5, loaded.Font.SizePoints);
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task SaveCreatesParentAndRoundTripsNormalizedValues()
    {
        string path = Path.Combine(directory, "new", "UWSC.INI");
        await new ScriptLogDefaults(200_000).SaveAsync(path);
        ScriptLogDefaults loaded = await ScriptLogDefaults.LoadAsync(path);
        Assert.Equal(100_000, loaded.MaximumLines);
        Assert.Equal("ＭＳ ゴシック", loaded.Font.Family);
        Assert.Equal(10, loaded.Font.SizePoints);
    }

    [Fact]
    public async Task CancelledSaveDoesNotCreateDirectoryOrChangeExistingBytes()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        string path = Path.Combine(directory, "new", "UWSC.INI");
        var settings = new ScriptLogDefaults(17, new ScriptLogFont("Arial", 11));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        path = Path.Combine(directory, "UWSC.INI");
        byte[] original = Encoding.UTF8.GetBytes("[SET]\nLogLines=5\nLogFont=Consolas,14");
        await File.WriteAllBytesAsync(path, original);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedReplacementKeepsBothOldValuesAndRemovesTemporaryFile()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        byte[] original = Encoding.UTF8.GetBytes("[SET]\nLogLines=5\nLogFont=Arial,14");
        await File.WriteAllBytesAsync(path, original);
        using (var locked = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ScriptLogDefaults(9, new ScriptLogFont("Consolas", 11))
                    .SaveAsync(path).AsTask());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesAndWriteIniShareTheSameMutationGate()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path, "[SET]\nKeep=yes\n");
        await using IScriptFileSession session = new ScriptFiles().CreateSession();
        Task[] saves = [.. Enumerable.Range(1, 20).Select(index =>
            new ScriptLogDefaults(index, new ScriptLogFont("Arial", index))
                .SaveAsync(path).AsTask())];
        Task<Value> write = session.InvokeAsync("WRITEINI",
            [Value.String("SET"), Value.String("Other"), Value.String("kept"),
                Value.String(path)], default).AsTask();
        await Task.WhenAll(saves.Append(write));
        ScriptLogDefaults loaded = await ScriptLogDefaults.LoadAsync(path);
        Assert.Equal(loaded.MaximumLines, loaded.Font.SizePoints);
        string document = await File.ReadAllTextAsync(path);
        Assert.Contains("Keep=yes", document);
        Assert.Contains("Other=kept", document);
    }
}
