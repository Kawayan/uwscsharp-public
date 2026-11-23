using System.Text;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopWindowSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-stop-size-" + Guid.NewGuid());

    public StopWindowSettingsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("220,61", 220, 61)]
    [InlineData("440,122", 440, 122)]
    [InlineData("620,240", 620, 240)]
    [InlineData(" 440 ,122 ", 440, 122)]
    [InlineData("220.9,61.9", 220, 61)]
    [InlineData("440,122,99", 440, 122)]
    [InlineData("1,1", 1, 1)]
    [InlineData("0,61", 0, 0)]
    [InlineData("-1,61", 0, 0)]
    [InlineData("bad", 0, 0)]
    [InlineData("220", 0, 0)]
    [InlineData("220,", 0, 0)]
    [InlineData("0.5,61", 0, 0)]
    [InlineData("NaN,61", 0, 0)]
    [InlineData("220,Infinity", 0, 0)]
    [InlineData("1e308,61", 0, 0)]
    [InlineData("1e3,4e2", 1000, 400)]
    [InlineData("2147483647,61", int.MaxValue, 61)]
    public async Task ReadsPhysicalDimensionsWithoutApplyingScreenLimits(
        string? value, int width, int height)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "[SET]\n" + (value is null ? "" : "StopSize=" + value + "\n");
        await File.WriteAllTextAsync(path, document);
        ApplicationWindowSize? size = (await ApplicationWindowSettings.LoadAsync(path)).StopSize;
        if (width == 0)
        {
            Assert.Null(size);
        }
        else
        {
            Assert.Equal(width, size!.WidthPixels);
            Assert.Equal(height, size.HeightPixels);
        }

        ApplicationWindowSize? startupSize = (await ScriptStartupDefaults.LoadAsync(path)).StopSize;
        Assert.Equal(size?.WidthPixels, startupSize?.WidthPixels);
        Assert.Equal(size?.HeightPixels, startupSize?.HeightPixels);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task CommitsFourSettingsAndPreservesEncodingAndDuplicateKeys(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = format switch
        {
            "utf16" => Encoding.Unicode,
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "; 日本語\r\n[SET]\r\nStayOnTop=0\r\nStopSize=220,61\r\n"
            + "StopSize=440,122\r\nOther=kept\r\nLogLines=3\r\n";
        await File.WriteAllTextAsync(path, document, encoding);
        await new ApplicationWindowSettings(true, new ScriptDialogFont("Arial", 18),
            new ApplicationWindowSize(900, 650), new ApplicationWindowSize(1000, 700)).SaveAsync(path);
        ApplicationWindowSettings saved = await ApplicationWindowSettings.LoadAsync(path);
        Assert.True(saved.StayOnTop);
        Assert.Equal("Arial", saved.DialogFont!.Family);
        Assert.Equal(1000, saved.StopSize!.WidthPixels);
        Assert.Equal(700, saved.StopSize.HeightPixels);
        Assert.Equal(900, saved.MainSize!.WidthPixels);
        string text = await File.ReadAllTextAsync(path, encoding);
        Assert.Contains("StopSize=1000,700\r\nStopSize=440,122", text);
        Assert.Contains("; 日本語", text);
        Assert.Contains("Other=kept\r\nLogLines=3", text);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        await new ApplicationWindowSettings().SaveAsync(path);
        await new ApplicationWindowSettings(true, new ScriptDialogFont()).SaveAsync(path);
        await new ApplicationWindowSettings(false, null,
            new ApplicationWindowSize(700, 500)).SaveAsync(path);
        Assert.Equal(1000, (await ApplicationWindowSettings.LoadAsync(path)).StopSize!.WidthPixels);
        ScriptStartupDefaults first = await ScriptStartupDefaults.LoadAsync(path);
        await new ApplicationWindowSettings(false, null, null,
            new ApplicationWindowSize(600, 400)).SaveAsync(path);
        ScriptStartupDefaults second = await ScriptStartupDefaults.LoadAsync(path);
        Assert.Equal(1000, first.StopSize!.WidthPixels);
        Assert.Equal(700, first.StopSize.HeightPixels);
        Assert.Equal(600, second.StopSize!.WidthPixels);
        Assert.Equal(400, second.StopSize.HeightPixels);
    }

    [Fact]
    public async Task CreatesMissingIniAndSerializesConcurrentLogUpdates()
    {
        string path = Path.Combine(directory, "nested", "UWSC.INI");
        Assert.Null((await ApplicationWindowSettings.LoadAsync(path)).StopSize);
        var settings = new ApplicationWindowSettings(false, new ScriptDialogFont("Arial", 18),
            new ApplicationWindowSize(900, 650), new ApplicationWindowSize(1000, 700));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        await Task.WhenAll(settings.SaveAsync(path).AsTask(),
            new ScriptLogDefaults(25, new ScriptLogFont("Consolas", 11)).SaveAsync(path).AsTask());
        ApplicationWindowSettings saved = await ApplicationWindowSettings.LoadAsync(path);
        Assert.Equal(1000, saved.StopSize!.WidthPixels);
        Assert.Equal(700, saved.StopSize.HeightPixels);
        ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(path);
        Assert.Equal("Arial,18", startup.DefaultFont);
        Assert.Equal(25, startup.Log.MaximumLines);
        Assert.Equal("Consolas", startup.Log.Font.Family);
    }

    [Fact]
    public async Task CancellationAndReplacementFailureKeepAllValues()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path, "[SET]\nStayOnTop=0\nDefaultFont=Consolas,11\nStopSize=800,600");
        byte[] before = await File.ReadAllBytesAsync(path);
        var settings = new ApplicationWindowSettings(true, new ScriptDialogFont("Arial", 18),
            new ApplicationWindowSize(900, 650), new ApplicationWindowSize(1000, 700));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => settings.SaveAsync(path).AsTask());
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

}
