using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopFormOptionsTests
{
    [Fact]
    public async Task RepeatedOptionsShareOneSessionAndRestoreAfterOutput()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            OPTION TOPSTOPFORM,NOSTOPHOTKEY
            PRINT "during"
            OPTION TOPSTOPFORM
            """, host);
        Assert.Equal(1, host.Created);
        Assert.Equal(["TopMost", "DisableStopHotkey", "during", "TopMost", "restore"], host.Events);
    }

    [Theory]
    [InlineData("TOPSTOPFORM=TRUE")]
    [InlineData("NOSTOPHOTKEY=FALSE")]
    [InlineData("POSITION=1,TOPSTOPFORM")]
    public async Task InvalidSyntaxNeverCreatesASession(string option)
    {
        var host = new Host();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("OPTION " + option, host));
        Assert.Equal(0, host.Created);
    }

    [Fact]
    public async Task HostWithoutOptionsReportsTheStatementLine()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("PRINT 1\nOPTION TOPSTOPFORM", _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task UnusedOptionsDoNotCreateASession()
    {
        var host = new Host();
        await new Interpreter().RunAsync("PRINT 1", host);
        Assert.Equal(0, host.Created);
        Assert.Equal(["1"], host.Events);
    }

    [Fact]
    public async Task ErrorRestoresAndKeepsThePrimaryException()
    {
        var host = new Host { FailRestore = true };
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("OPTION TOPSTOPFORM\nPRINT missing", host));
        Assert.Equal(2, error.Line);
        Assert.True(error.Data.Contains("UWSCSharp.CleanupErrors"));
        Assert.Equal(["TopMost", "restore"], host.Events);
    }

    [Fact]
    public async Task CancellationRestoresWithoutUsingTheCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new Host { OnApply = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("OPTION NOSTOPHOTKEY\nPRINT 1", host, cancellation.Token));
        Assert.Equal(["DisableStopHotkey", "restore"], host.Events);
    }

    [Fact]
    public async Task ExitExitRestoresTheSession()
    {
        var host = new Host();
        await new Interpreter().RunAsync("OPTION TOPSTOPFORM\nEXITEXIT\nPRINT 1", host);
        Assert.Equal(["TopMost", "restore"], host.Events);
    }

    [Fact]
    public async Task WorkerAndEvalShareTheRootSession()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            PUBLIC finished=FALSE
            THREAD Child()
            WHILE finished=FALSE
                SLEEP(0.01)
            WEND
            PRINT "after child"
            EVAL("ApplyOption()")
            FUNCTION ApplyOption()
                OPTION TOPSTOPFORM
            FEND
            PROCEDURE Child()
                OPTION NOSTOPHOTKEY
                finished=TRUE
            FEND
            """, host);
        Assert.Equal(1, host.Created);
        Assert.Equal(["DisableStopHotkey", "after child", "TopMost", "restore"], host.Events);
    }


    [Fact]
    public async Task CalledFileDoesNotRestoreBeforeItsParentEnds()
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uwsc-stop-options-" + Guid.NewGuid()));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        string child = Path.Combine(directory, "child.uws");
        string main = Path.Combine(directory, "main.uws");
        try
        {
            await File.WriteAllTextAsync(child, "OPTION NOSTOPHOTKEY");
            await File.WriteAllTextAsync(main, "CALL child\nPRINT \"parent\"\nOPTION TOPSTOPFORM");
            var host = new Host();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            Assert.Equal(1, host.Created);
            Assert.Equal(["DisableStopHotkey", "parent", "TopMost", "restore"], host.Events);
        }
        finally
        {
            File.Delete(child);
            File.Delete(main);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task FailedApplyStillRestoresTheSession()
    {
        var host = new Host { OnApply = () => throw new InvalidOperationException("適用失敗の試験です。") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter().RunAsync("OPTION TOPSTOPFORM", host));
        Assert.Equal(["TopMost", "restore"], host.Events);
    }

    private sealed class Host : IScriptStopFormOptionsHost
    {
        internal List<string> Events { get; } = [];

        internal int Created;

        internal bool FailRestore
        {
            get; init;
        }

        internal Action? OnApply
        {
            get; init;
        }

        public ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created++;
            return ValueTask.FromResult<IScriptStopFormOptionsSession>(new Session(this));
        }

        public ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteOutputAsync(string text, CancellationToken cancellationToken)
        {
            Events.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.Delay(duration, cancellationToken);

        private sealed class Session(Host owner) : IScriptStopFormOptionsSession
        {
            public ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.Events.Add(option.ToString());
                owner.OnApply?.Invoke();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.Events.Add("restore");
                if (owner.FailRestore)
                {
                    throw new InvalidOperationException("復元失敗の試験です。");
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
