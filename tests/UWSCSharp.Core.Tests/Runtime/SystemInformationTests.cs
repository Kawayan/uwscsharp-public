using System.Reflection;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SystemInformationTests
{
    [Fact]
    public async Task DefaultsExposeSystemDirectoryRealVersionAndFreeEdition()
    {
        string expectedVersion = typeof(Interpreter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PRINT GET_SYS_DIR
            PRINT GET_UWSC_VER
            PRINT GET_UWSC_PRO
            PRINT GET_UWSC_VER+" suffix"
            """, output.Add);
        Assert.Equal([Environment.SystemDirectory, expectedVersion,
            "0", expectedVersion + " suffix"], output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\日本語 folder\\")]
    [InlineData("  directory  ")]
    public async Task ExplicitSystemDirectoryIsPreserved(string directory)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT get_sys_dir", output.Add,
            default, new ScriptRunOptions([]) { SystemDirectory = directory });
        Assert.Equal([directory], output);
    }

    [Theory]
    [InlineData("GET_SYS_DIR")]
    [InlineData("GET_UWSC_VER")]
    [InlineData("GET_UWSC_PRO")]
    public async Task MetadataAssignmentAndLocalShadowingResetOnNextRun(string name)
    {
        var runner = new Interpreter();
        var options = new ScriptRunOptions([]) { SystemDirectory = "system" };
        List<string> initial = [];
        await runner.RunAsync("PRINT " + name, initial.Add, default, options);
        List<string> output = [];
        await runner.RunAsync($$"""
            OPTION EXPLICIT
            {{name}}="changed"
            Probe("parameter")
            PRINT {{name}}
            PROCEDURE Probe({{name}})
                PRINT {{name}}
            FEND
            """, output.Add, default, options);
        Assert.Equal(["parameter", "changed"], output);
        List<string> next = [];
        await runner.RunAsync("PRINT " + name, next.Add, default, options);
        Assert.Equal(initial, next);
        Assert.Equal("system", options.SystemDirectory);
    }

    [Theory]
    [InlineData("GET_SYS_DIR")]
    [InlineData("GET_CUR_DIR")]
    [InlineData("GET_UWSC_VER")]
    [InlineData("GET_UWSC_PRO")]
    public async Task GlobalRedeclarationFailsBeforeInitializer(string name)
    {
        foreach (string declaration in new[] { "PUBLIC", "CONST" })
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync($"\n{declaration} {name}=1/0", _ => { }));
            Assert.Equal(2, error.Line);
            Assert.Contains("再定義", error.Message);
        }
    }

    [Fact]
    public async Task CurrentDirectoryIsReadFromSessionEveryTime()
    {
        var session = new FileSession { Directory = "first" };
        List<string> output = [];
        await new Interpreter(files: new Files(() => session)).RunAsync("""
            OPTION EXPLICIT
            PUBLIC initial=GET_CUR_DIR
            PRINT initial
            PRINT get_cur_dir
            PRINT EVAL("GET_CUR_DIR")
            """, text =>
            {
                output.Add(text);
                session.Directory = "second";
            });
        Assert.Equal(["first", "second", "second"], output);
        Assert.Equal(3, session.Reads);
    }

    [Fact]
    public async Task CurrentDirectoryWithoutFilesUsesProcessValue()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT GET_CUR_DIR", output.Add);
        Assert.Equal([Environment.CurrentDirectory], output);
    }

    [Fact]
    public async Task CurrentDirectoryCannotBeAssignedButCanBeLocallyShadowed()
    {
        var session = new FileSession();
        var runner = new Interpreter(files: new Files(() => session));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            runner.RunAsync("GET_CUR_DIR=\"other\"", _ => { }));
        Assert.Contains("読み取り専用", error.Message);
        List<string> output = [];
        await runner.RunAsync("DIM GET_CUR_DIR=\"local\"\nPRINT GET_CUR_DIR",
            output.Add);
        Assert.Equal(["local"], output);
        Assert.Equal(0, session.Reads);
    }

    [Fact]
    public async Task ConcurrentRunsUseTheirOwnDirectorySessions()
    {
        var ready = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0;
        var sessions = new Queue<FileSession>([
            new() { Directory = "first" }, new() { Directory = "second" }
        ]);
        var runner = new Interpreter(files: new Files(() => sessions.Dequeue()));
        ScriptProgram program = ScriptCompiler.Compile(
            "SLEEP(0.001)\nPRINT GET_CUR_DIR");
        List<string> first = [];
        List<string> second = [];
        void Arrive()
        {
            if (Interlocked.Increment(ref waiting) == 2)
            {
                ready.SetResult();
            }
        }

        Task a = runner.RunAsync(program,
            new WaitingHost(first.Add, Arrive, release.Task));
        Task b = runner.RunAsync(program,
            new WaitingHost(second.Add, Arrive, release.Task));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(a.IsCompleted);
            Assert.False(b.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }

        await Task.WhenAll(a, b);
        Assert.Equal(["first"], first);
        Assert.Equal(["second"], second);
    }

    [Fact]
    public async Task DirectoryIoFailureIsCaughtButUnknownFailurePropagates()
    {
        var session = new FileSession { Failure = new IOException("directory failed") };
        List<string> output = [];
        var runner = new Interpreter(files: new Files(() => session));
        await runner.RunAsync("""
            TRY
                PRINT GET_CUR_DIR
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, output.Add);
        Assert.Equal(["caught"], output);
        var unknown = new InvalidOperationException("unknown host failure");
        session.Failure = unknown;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("PRINT GET_CUR_DIR", _ => { }));
        Assert.Same(unknown, actual);
    }

    [Fact]
    public async Task CancellationAfterDirectoryReadStopsBeforeOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FileSession { OnRead = cancellation.Cancel };
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: new Files(() => session)).RunAsync(
                "PRINT GET_CUR_DIR", output.Add, cancellation.Token));
        Assert.Empty(output);
    }


    [Fact]
    public async Task InvalidKindOfOsCallShapeDoesNotEvaluateArguments()
    {
        List<string> output = [];
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync("""
            PRINT KINDOFOS(Side(),Side())
            FUNCTION Side()
                PRINT "side"
                RESULT=1
            FEND
            """, output.Add));
        Assert.Empty(output);
        Assert.False(Names.CanDeclare("kindofos"));
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("KINDOFOS()"));
    }

    private sealed class Files(Func<FileSession> create) : IScriptFiles
    {
        public IScriptFileSession CreateSession() => create();
    }

    private sealed class FileSession : IScriptFileSession
    {
        internal string Directory = "directory";
        internal int Reads;
        internal Exception? Failure;
        internal Action? OnRead;

        public string CurrentDirectory
        {
            get
            {
                Reads++;
                if (Failure is not null)
                {
                    throw Failure;
                }

                OnRead?.Invoke();
                return Directory;
            }
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingHost(
        Action<string> output, Action arrive, Task release) : IScriptHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            output(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            arrive();
            return release.WaitAsync(token);
        }
    }
}
