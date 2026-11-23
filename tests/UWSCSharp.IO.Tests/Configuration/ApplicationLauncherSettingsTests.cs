using System.Text;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ApplicationLauncherSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-launcher-position-" + Guid.NewGuid());

    public ApplicationLauncherSettingsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(null, -1, -1)]
    [InlineData("", -1, -1)]
    [InlineData("-1,-1", -1, -1)]
    [InlineData("0,0", 0, 0)]
    [InlineData("120,240", 120, 240)]
    [InlineData("-1920,-100", -1920, -100)]
    [InlineData("-1,50", -1, 50)]
    [InlineData("50,-1", 50, -1)]
    [InlineData(" +120 , -240 ", 120, -240)]
    [InlineData("-2147483648,2147483647", int.MinValue, int.MaxValue)]
    [InlineData("2147483648,0", -1, -1)]
    [InlineData("0,-2147483649", -1, -1)]
    [InlineData("120", -1, -1)]
    [InlineData("120,", -1, -1)]
    [InlineData(",240", -1, -1)]
    [InlineData("120,240,360", -1, -1)]
    [InlineData("120.5,240", -1, -1)]
    [InlineData("1e2,240", -1, -1)]
    [InlineData("NaN,240", -1, -1)]
    [InlineData("120,240\nPosition=360,480", 120, 240)]
    public async Task ReadsCoordinatesWithoutEditingTheDocument(
        string? text, int x, int y)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "[LUNCHMENU]\n"
            + (text is null ? "" : "Position=" + text + "\n");
        await File.WriteAllTextAsync(path, document);
        ApplicationLauncherSettings settings =
            await ApplicationLauncherSettings.LoadAsync(path);
        Assert.Equal(x, settings.X);
        Assert.Equal(y, settings.Y);
        Assert.Equal(x == -1 && y == -1, settings.UseCursorPosition);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task SavesOnlyPositionAndPreservesTheDocument(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = format switch
        {
            "utf16" => Encoding.Unicode,
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "; 日本語\r\n[LUNCHMENU]\r\nPosition=-1,-1\r\n"
            + "Position=30,40\r\nUnknown=kept\r\n[SET]\r\nMainSize=1000,700\r\n";
        await File.WriteAllTextAsync(path, document, encoding);
        await new ApplicationLauncherSettings(-1920, 120).SaveAsync(path);
        string expected = document.Replace("Position=-1,-1", "Position=-1920,120");
        Assert.Equal(expected, await File.ReadAllTextAsync(path, encoding));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)),
            await File.ReadAllBytesAsync(path));
        ApplicationLauncherSettings loaded = await ApplicationLauncherSettings.LoadAsync(path);
        Assert.Equal(-1920, loaded.X);
        Assert.Equal(120, loaded.Y);
        Assert.False(loaded.UseCursorPosition);
        await new ApplicationLauncherSettings().SaveAsync(path);
        Assert.Equal(document, await File.ReadAllTextAsync(path, encoding));
        Assert.True((await ApplicationLauncherSettings.LoadAsync(path)).UseCursorPosition);
    }

    [Fact]
    public async Task MissingFilesCancellationAndReplacementFailureKeepExistingState()
    {
        string path = Path.Combine(directory, "nested", "UWSC.INI");
        Assert.True((await ApplicationLauncherSettings.LoadAsync(path)).UseCursorPosition);
        Assert.False(File.Exists(path));
        var settings = new ApplicationLauncherSettings(120, 240);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApplicationLauncherSettings.LoadAsync(path, stop.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        await settings.SaveAsync(path);
        byte[] before = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ApplicationLauncherSettings().SaveAsync(path).AsTask());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".uwsc-ini-*.tmp"));
    }
}
