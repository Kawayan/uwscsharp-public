using System.Text;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ApplicationWindowSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-window-settings-" + Guid.NewGuid());

    public ApplicationWindowSettingsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", false)]
    [InlineData("-1", false)]
    [InlineData("1.0", false)]
    [InlineData("1.5", false)]
    [InlineData(" 1 ", true)]
    [InlineData("01", false)]
    [InlineData("+1", false)]
    [InlineData("true", false)]
    [InlineData("bad", false)]
    [InlineData("2147483648", false)]
    [InlineData("0\nStayOnTop=1", false)]
    [InlineData("1\nStayOnTop=0", true)]
    public async Task OnlyTheLiteralOneEnablesStayOnTop(string? value, bool expected)
    {
        string path = Path.Combine(directory, "UWSC.INI");
        string document = "[SET]\n" + (value is null ? "" : "StayOnTop=" + value);
        await File.WriteAllTextAsync(path, document);
        Assert.Equal(expected, (await ApplicationWindowSettings.LoadAsync(path)).StayOnTop);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingFileAndLaterDuplicateSectionsDoNotEnableTheSetting()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        Assert.False((await ApplicationWindowSettings.LoadAsync(path)).StayOnTop);
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, "[SET]\nOther=kept\n[SET]\nStayOnTop=1");
        Assert.False((await ApplicationWindowSettings.LoadAsync(path)).StayOnTop);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task SavesBothStatesAndPreservesOtherSettings(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = format switch
        {
            "utf16" => Encoding.Unicode,
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        string original = "; 日本語\r\n[ set ]\r\nStayOnTop=0\r\n"
            + "LogLines=3\r\nLogFont=Consolas,12\r\nOther=kept";
        await File.WriteAllTextAsync(path, original, encoding);
        await new ApplicationWindowSettings(true).SaveAsync(path);
        Assert.True((await ApplicationWindowSettings.LoadAsync(path)).StayOnTop);
        byte[] preamble = encoding.GetPreamble();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(preamble));
        Assert.Equal(original.Replace("StayOnTop=0", "StayOnTop=1"),
            encoding.GetString(bytes.AsSpan(preamble.Length)));
        await new ApplicationWindowSettings().SaveAsync(path);
        Assert.Equal(original, await File.ReadAllTextAsync(path, encoding));
    }

    [Fact]
    public async Task WindowAndFontSettingsAreCommittedTogetherAndCancellationKeepsBoth()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path,
            "; retained\n[SET]\nStayOnTop=0\nDefaultFont=Consolas,12.9\n"
            + "DefaultFont=Tahoma,10\nLogLines=3\n[SET]\nStayOnTop=1");
        ApplicationWindowSettings initial = await ApplicationWindowSettings.LoadAsync(path);
        Assert.Equal("Consolas", initial.DialogFont!.Family);
        Assert.Equal(12, initial.DialogFont.SizePoints);
        var replacement = new ApplicationWindowSettings(true, new ScriptDialogFont(" Arial ", 18));
        byte[] before = await File.ReadAllBytesAsync(path);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            replacement.SaveAsync(path, stop.Token).AsTask());
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => replacement.SaveAsync(path).AsTask());
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await replacement.SaveAsync(path);
        ApplicationWindowSettings saved = await ApplicationWindowSettings.LoadAsync(path);
        Assert.True(saved.StayOnTop);
        Assert.Equal("Arial", saved.DialogFont!.Family);
        Assert.Equal(18, saved.DialogFont.SizePoints);
        Assert.Equal("Arial,18", (await ScriptStartupDefaults.LoadAsync(path)).DefaultFont);
        string document = await File.ReadAllTextAsync(path);
        Assert.Contains("DefaultFont=Tahoma,10", document);
        Assert.Contains("[SET]\nStayOnTop=1", document);
        Assert.Contains("; retained", document);
        Assert.Contains("LogLines=3", document);
        await new ApplicationWindowSettings().SaveAsync(path);
        Assert.Equal("Arial,18", (await ScriptStartupDefaults.LoadAsync(path)).DefaultFont);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    public async Task DialogFontSaveKeepsEncodingAndConcurrentLogUpdates(string format)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = format switch
        {
            "utf16" => Encoding.Unicode,
            "cp932" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(true)
        };
        string path = Path.Combine(directory, "UWSC.INI");
        await File.WriteAllTextAsync(path, "; 日本語\r\n[SET]\r\nOther=kept\r\n", encoding);
        await Task.WhenAll(
            new ApplicationWindowSettings(true, new ScriptDialogFont("ＭＳ ゴシック", 14))
                .SaveAsync(path).AsTask(),
            new ScriptLogDefaults(25, new ScriptLogFont("Consolas", 11.5))
                .SaveAsync(path).AsTask());
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Contains("; 日本語\r\n[SET]\r\nOther=kept\r\n", await File.ReadAllTextAsync(path, encoding));
        ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(path);
        Assert.Equal("ＭＳ ゴシック,14", startup.DefaultFont);
        Assert.Equal(25, startup.Log.MaximumLines);
        Assert.Equal(11.5, startup.Log.Font.SizePoints);
        Assert.True((await ApplicationWindowSettings.LoadAsync(path)).StayOnTop);
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }

    [Theory]
    [InlineData("", 9)]
    [InlineData(" ", 9)]
    [InlineData("Arial,Consolas", 9)]
    [InlineData("Arial\nOther=1", 9)]
    [InlineData("Arial", 0)]
    [InlineData("Arial", -1)]
    [InlineData("Arial", int.MaxValue)]
    public void InvalidDialogFontCannotReachTheIniWriter(string family, int points)
        => Assert.ThrowsAny<ArgumentException>(() => new ScriptDialogFont(family, points));

    [Fact]
    public async Task CancellationAndReadOrReplacementFailuresKeepThePreviousState()
    {
        string path = Path.Combine(directory, "UWSC.INI");
        byte[] original = Encoding.UTF8.GetBytes("[SET]\nStayOnTop=1\nLogLines=3");
        await File.WriteAllBytesAsync(path, original);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApplicationWindowSettings.LoadAsync(path, stop.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ApplicationWindowSettings().SaveAsync(path, stop.Token).AsTask());
        using (var locked = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                ApplicationWindowSettings.LoadAsync(path).AsTask());
        }

        using (var locked = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ApplicationWindowSettings().SaveAsync(path).AsTask());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-ini-*.tmp"));
    }
}
