using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogOptionIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-option-" + Guid.NewGuid());

    public LogOptionIntegrationTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    private Task<int> Run(string source) => ScriptLogHost.RunScriptAsync(
        new CallbackScriptHost(_ => { }), Path.Combine(directory, "main.uws"),
        host => new Interpreter().RunWithExitCodeAsync(source, host,
            default, ScriptRunOptions.Empty));

    [Fact]
    public async Task OptionsControlRealLogPathModeAndRetainedTail()
    {
        await Run("""
            OPTION LOGPATH="selected,a.log", LOGFILE=6, LOGLINES=2
            PRINT "a"
            PRINT "b"
            PRINT "c"
            """);
        Assert.Equal("b\r\nc\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "selected,a.log")));
        Assert.False(File.Exists(Path.Combine(directory, "UWSC.LOG")));
    }

    [Fact]
    public async Task PathAndModeChangesFlushEarlierOutputBeforeSwitching()
    {
        await Run("""
            OPTION LOGFILE=2
            PRINT "default"
            OPTION LOGPATH="other.log"
            PRINT "other"
            OPTION LOGFILE=1
            PRINT "visible only"
            OPTION LOGFILE=2, LOGPATH=""
            PRINT "default again"
            """);
        Assert.Equal("default\r\ndefault again\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "UWSC.LOG")));
        Assert.Equal("other\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "other.log")));
    }

    [Fact]
    public async Task DirectoryPathUsesDefaultFileName()
    {
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        await Run("""
            OPTION LOGPATH="logs", LOGFILE=2
            PRINT "directory"
            """);
        Assert.Equal("directory\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "logs", "UWSC.LOG")));
    }

    [Fact]
    public async Task CallSharesLogConfigurationForWholeExecution()
    {
        string child = Path.Combine(directory, "child.uws");
        string main = Path.Combine(directory, "main.uws");
        await File.WriteAllTextAsync(child, """
            OPTION LOGPATH="child.log", LOGFILE=2
            PRINT "child"
            """);
        await File.WriteAllTextAsync(main, """
            OPTION LOGFILE=2
            PRINT "parent before"
            CALL child
            PRINT "parent after"
            """);
        await ScriptLogHost.RunScriptAsync(new CallbackScriptHost(_ => { }), main,
            host => new Interpreter().RunWithExitCodeAsync(
                ScriptCompiler.CompileFile(main), host, default, ScriptRunOptions.Empty));
        Assert.Equal("parent before\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "UWSC.LOG")));
        Assert.Equal("child\r\nparent after\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "child.log")));
    }

    [Fact]
    public async Task DefaultLoggingUsesFreshConfigurationForEachRun()
    {
        await Run("OPTION LOGFILE=1\nPRINT 1");
        Assert.False(File.Exists(Path.Combine(directory, "UWSC.LOG")));
        await Run("PRINT \"next\"");
        Assert.EndsWith("  next\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "UWSC.LOG")));
    }

    [Fact]
    public async Task InvalidReconfigurationKeepsPreviousSettingsAndOutput()
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            Path.Combine(directory, "UWSC.LOG"), mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("pending", default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: -1), default));
        await host.FlushAsync();
        Assert.Equal("pending\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "UWSC.LOG")));
    }

    [Fact]
    public async Task ScriptLoggingReplacesIsolatedSurrogatesWithoutFailingPrint()
    {
        await Run("OPTION LOGFILE=2\nPRINT CHR($D800)\nPRINT CHR($DC00)");
        Assert.Equal("\ufffd\r\n\ufffd\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, "UWSC.LOG")));
    }
}
