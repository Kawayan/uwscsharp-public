using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogCallLifetimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-call-" + Guid.NewGuid());

    public LogCallLifetimeTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("OPTION LOGFILE=1\nPRINT \"child\"", "", false)]
    [InlineData("OPTION LOGLINES=2\nPRINT \"child1\"\nPRINT \"child2\"",
        "child2\r\nparent after\r\n", true)]
    [InlineData("OPTION LOGLINES=2\nPRINT \"child\"\nEXIT\nPRINT \"skipped\"",
        "child\r\nparent after\r\n", true)]
    [InlineData("OPTION LOGLINES=2\nPRINT \"child\"\nEXITEXIT\nPRINT \"skipped\"",
        "child\r\n", true)]
    public async Task ChildChangesSurviveReturnOrExit(
        string childSource, string expected, bool createsLog)
    {
        await RunAsync("CALL child\nPRINT \"parent after\"", childSource);
        string path = Path.Combine(directory, "parent.log");
        Assert.Equal(createsLog, File.Exists(path));
        if (createsLog)
        {
            Assert.Equal(expected, await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task GrandchildConfigurationSurvivesBothReturns()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "grandchild.uws"), """
            OPTION LOGFILE=2
            PRINT "grandchild"
            """);
        await RunAsync("CALL child\nPRINT \"parent after\"", """
            OPTION LOGFILE=1
            CALL grandchild
            PRINT "child after"
            """);
        Assert.Equal("grandchild\r\nchild after\r\nparent after\r\n",
            await File.ReadAllTextAsync(Path.Combine(directory, "parent.log")));
    }

    [Fact]
    public async Task CaughtChildFailureKeepsAppliedLimit()
    {
        await RunAsync("""
            TRY
                CALL child
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT "parent after"
            """, """
            OPTION LOGLINES=2
            PRINT "child"
            DIM values[0]
            PRINT values[2]
            """);
        Assert.Equal("caught\r\nparent after\r\n",
            await File.ReadAllTextAsync(Path.Combine(directory, "parent.log")));
    }

    [Fact]
    public async Task ChildRelativeLogPathUsesMainDirectoryAndSurvivesReturn()
    {
        await RunAsync("CALL sub/child\nPRINT \"parent after\"", """
            OPTION LOGPATH="child.log"
            PRINT "child"
            """, "sub/child.uws");
        Assert.Equal("child\r\nparent after\r\n",
            await File.ReadAllTextAsync(Path.Combine(directory, "child.log")));
        Assert.False(File.Exists(Path.Combine(directory, "sub", "child.log")));
        Assert.False(File.Exists(Path.Combine(directory, "parent.log")));
    }

    private async Task RunAsync(string mainBody, string childSource,
        string childName = "child.uws")
    {
        string main = Path.Combine(directory, "main.uws");
        string child = Path.Combine(directory, childName);
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        await File.WriteAllTextAsync(child, childSource);
        await File.WriteAllTextAsync(main, """
            OPTION LOGPATH="parent.log", LOGFILE=2, LOGLINES=30
            """ + "\n" + mainBody);
        int exitCode = await ScriptLogHost.RunScriptAsync(
            new CallbackScriptHost(_ => { }), main,
            host => new Interpreter().RunWithExitCodeAsync(
                ScriptCompiler.CompileFile(main), host,
                default, ScriptRunOptions.Empty));
        Assert.Equal(0, exitCode);
    }
}
