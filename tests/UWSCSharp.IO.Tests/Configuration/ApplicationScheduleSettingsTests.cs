using System.Text;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ApplicationScheduleSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-schedule-settings-" + Guid.NewGuid());

    public ApplicationScheduleSettingsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(null, 40)]
    [InlineData("", 40)]
    [InlineData("40", 40)]
    [InlineData("200", 200)]
    [InlineData("400", 400)]
    [InlineData("401", 400)]
    [InlineData("1", 40)]
    [InlineData("-1", 40)]
    [InlineData(" 80 ", 80)]
    [InlineData("80.5", 40)]
    [InlineData("1e2", 40)]
    [InlineData("NaN", 40)]
    [InlineData("2147483648", 40)]
    [InlineData("80\nMaxNumber=200", 80)]
    public async Task ReadsCapacityWithoutEditingTheDocument(string? text, int expected)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "[SCHEDULE]\n" + (text is null ? "" : "MaxNumber=" + text + "\n");
        await File.WriteAllTextAsync(path, document);
        Assert.Equal(expected, (await ApplicationScheduleSettings.LoadAsync(path)).Capacity);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task SavesOnlyCapacityAndPreservesTheDocument(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = format switch
        {
            "utf16" => Encoding.Unicode,
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "; 日本語\r\n[SCHEDULE]\r\nMaxNumber=40\r\n"
            + "MaxNumber=60\r\nUnknown=kept\r\n[SET]\r\nMainSize=1000,700\r\n";
        await File.WriteAllTextAsync(path, document, encoding);
        var settings = new ApplicationScheduleSettings(200);
        await settings.SaveAsync(path);
        Assert.Equal(document.Replace("MaxNumber=40", "MaxNumber=200"),
            await File.ReadAllTextAsync(path, encoding));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Equal(200, (await ApplicationScheduleSettings.LoadAsync(path)).Capacity);
    }

    [Fact]
    public async Task MissingFilesCancellationAndReplacementFailureKeepExistingState()
    {
        string path = Path.Combine(directory, "nested", "UWSC.INI");
        Assert.Equal(40, (await ApplicationScheduleSettings.LoadAsync(path)).Capacity);
        Assert.False(File.Exists(path));
        var settings = new ApplicationScheduleSettings(80);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            settings.SaveAsync(path, stop.Token).AsTask());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        await settings.SaveAsync(path);
        byte[] before = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ApplicationScheduleSettings(200).SaveAsync(path).AsTask());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".uwsc-ini-*.tmp"));
    }

    [Theory]
    [InlineData(39)]
    [InlineData(401)]
    public void RejectsOutOfRangeApiValues(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ApplicationScheduleSettings(capacity));
}
