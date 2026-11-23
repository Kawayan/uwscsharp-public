using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class ScriptLogDefaultsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-defaults-" + Guid.NewGuid());

    public ScriptLogDefaultsTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    private string SettingsPath => Path.Combine(directory, "UWSC.INI");

    [Theory]
    [InlineData("", 2000)]
    [InlineData("0", 2000)]
    [InlineData("-1", 2000)]
    [InlineData("1", 1)]
    [InlineData("100000", 100000)]
    [InlineData("100001", 100000)]
    [InlineData("2147483647", 100000)]
    [InlineData("2147483648", 2000)]
    [InlineData("2.5", 2000)]
    [InlineData("NaN", 2000)]
    [InlineData("three", 2000)]
    public async Task StartupLimitUsesPositiveIntegerOrDefault(
        string text, int expected)
    {
        await File.WriteAllTextAsync(SettingsPath, "[SET]\nLogLines=" + text);
        ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(SettingsPath);
        Assert.Equal(expected, defaults.MaximumLines);
    }

    [Theory]
    [InlineData("", 2000, "one\r\ntwo\r\nfont probe\r\n")]
    [InlineData("1", 1, "font probe\r\n")]
    [InlineData("2", 2, "two\r\nfont probe\r\n")]
    [InlineData("0", 2000, "one\r\ntwo\r\nfont probe\r\n")]
    [InlineData("-1", 2000, "one\r\ntwo\r\nfont probe\r\n")]
    [InlineData("bad", 2000, "one\r\ntwo\r\nfont probe\r\n")]
    [InlineData("2147483648", 2000, "one\r\ntwo\r\nfont probe\r\n")]
    public async Task StartupSettingsControlSavedOutputAndViewLimit(
        string setting, int expectedLimit, string expectedLog)
    {
        await File.WriteAllTextAsync(SettingsPath,
            "[SET]\nLogLines=" + setting);
        ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(SettingsPath);
        var output = new ViewHost();
        int code = await ScriptLogHost.RunScriptWithDefaultsAsync(output,
            Path.Combine(directory, "main.uws"), defaults,
            host => new Interpreter().RunWithExitCodeAsync(
                "OPTION LOGFILE=6\nPRINT \"one\"\nPRINT \"two\"\nPRINT \"font probe\"",
                host, default, new ScriptRunOptions([])), _ => Assert.Fail());
        Assert.Equal(0, code);
        Assert.Equal([expectedLimit], output.Limits);
        Assert.Equal(expectedLog,
            await File.ReadAllTextAsync(Path.Combine(directory, "UWSC.LOG")));
        Assert.Equal("[SET]\nLogLines=" + setting,
            await File.ReadAllTextAsync(SettingsPath));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("ansi")]
    public async Task SharedIniReaderPreservesEncodingAndFirstKey(string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding selected = encoding switch
        {
            "utf16" => Encoding.Unicode,
            "ansi" => Encoding.GetEncoding(932),
            _ => new UTF8Encoding(false)
        };
        await File.WriteAllTextAsync(SettingsPath,
            "; 日本語\n[other]\nLogLines=9\n[ set ]\n loglines = 2\n"
            + "LogLines=4\n[SET]\nLogLines=5", selected);
        byte[] before = await File.ReadAllBytesAsync(SettingsPath);
        ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(SettingsPath);
        Assert.Equal(2, defaults.MaximumLines);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task MissingSettingsAndSectionUseDefaultWithoutCreatingFile()
    {
        Assert.Equal(2000, (await ScriptLogDefaults.LoadAsync(SettingsPath)).MaximumLines);
        Assert.False(File.Exists(SettingsPath));
        await File.WriteAllTextAsync(SettingsPath, "[Other]\nLogLines=2");
        Assert.Equal(2000, (await ScriptLogDefaults.LoadAsync(SettingsPath)).MaximumLines);
    }

    [Fact]
    public async Task CancellationAndReadFailurePreventExecution()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ScriptLogDefaults.LoadAsync(SettingsPath, stop.Token));
        Directory.CreateDirectory(SettingsPath);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () =>
            await ScriptLogDefaults.LoadAsync(SettingsPath));
    }

    [Fact]
    public async Task SnapshotAndOptionApplyToBothHistoryAndSavedRows()
    {
        await File.WriteAllTextAsync(SettingsPath, "[SET]\nLogLines=2");
        ScriptLogDefaults defaults = await ScriptLogDefaults.LoadAsync(SettingsPath);
        await File.WriteAllTextAsync(SettingsPath, "[SET]\nLogLines=1");
        string path = Path.Combine(directory, "main.uws");
        string logPath = Path.Combine(directory, "UWSC.LOG");
        var output = new ViewHost();
        int code = await ScriptLogHost.RunScriptWithDefaultsAsync(output,
            path, defaults, host => new Interpreter().RunWithExitCodeAsync(
                "OPTION LOGFILE=6\nPRINT 1\nPRINT 2\nPRINT 3", host, default, new ScriptRunOptions([])),
            _ => Assert.Fail());
        Assert.Equal(0, code);
        Assert.Equal([2], output.Limits);
        Assert.Equal("2\r\n3\r\n", await File.ReadAllTextAsync(logPath));
        File.Delete(logPath);

        output.Limits.Clear();
        defaults = await ScriptLogDefaults.LoadAsync(SettingsPath);
        await ScriptLogHost.RunScriptWithDefaultsAsync(output, path,
            defaults, host => new Interpreter().RunWithExitCodeAsync(
                "OPTION LOGFILE=6\nOPTION LOGLINES=3\nPRINT 1\nPRINT 2\nPRINT 3",
                host, default, new ScriptRunOptions([])),
            _ => Assert.Fail());
        Assert.Equal([1, 3], output.Limits);
        Assert.Equal("1\r\n2\r\n3\r\n", await File.ReadAllTextAsync(logPath));
        Assert.Equal("[SET]\nLogLines=1", await File.ReadAllTextAsync(SettingsPath));
    }

    [Fact]
    public async Task OptionZeroDisablesPositiveStartupLimit()
    {
        var output = new ViewHost();
        await ScriptLogHost.RunScriptWithDefaultsAsync(output,
            Path.Combine(directory, "main.uws"), new ScriptLogDefaults(2),
            host => new Interpreter().RunWithExitCodeAsync(
                "OPTION LOGLINES=0\nPRINT 1", host, default,
                new ScriptRunOptions([])), _ => Assert.Fail());
        Assert.Equal([2, 0], output.Limits);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task EmptyExecutionAppliesHistoryWithoutOutputOrFile()
    {
        var output = new ViewHost();
        await ScriptLogHost.RunScriptWithDefaultsAsync(output,
            Path.Combine(directory, "main.uws"), new ScriptLogDefaults(2),
            _ => Task.FromResult(0), _ => Assert.Fail());
        Assert.Equal([2], output.Limits);
        Assert.Empty(Directory.GetFiles(directory));
    }

    private sealed class ViewHost : IScriptLogViewHost
    {
        internal List<int> Limits { get; } = [];

        public ValueTask SetLogViewAsync(ScriptLogView view,
            CancellationToken token) => ValueTask.CompletedTask;

        public ValueTask SetLogHistoryLimitAsync(int maximumLines,
            CancellationToken token)
        {
            Limits.Add(maximumLines);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text,
            CancellationToken token) => ValueTask.CompletedTask;

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
