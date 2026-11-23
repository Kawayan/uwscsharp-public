using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExecutionContextTests
{
    [Fact]
    public async Task DefaultsDescribeHostAndAnonymousSource()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PRINT GET_WIN_DIR
            PRINT GET_UWSC_DIR
            PRINT "["+GET_UWSC_NAME+"]"
            """, output.Add);
        Assert.Equal([
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            "[]"
        ], output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\日本語 folder\\")]
    [InlineData("  path  ")]
    public async Task ExplicitDirectoriesArePreserved(string directory)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC win=GET_WIN_DIR, app=GET_UWSC_DIR
            PRINT win
            PRINT app
            PRINT get_uwsc_name
            """, output.Add, default, new ScriptRunOptions([])
        {
            WindowsDirectory = directory,
            ExecutableDirectory = directory,
            SourcePath = Path.Combine(Path.GetTempPath(), "日本語 script.uws")
        });
        Assert.Equal([directory, directory, "日本語 script.uws"], output);
    }

    [Theory]
    [InlineData("GET_WIN_DIR")]
    [InlineData("GET_UWSC_DIR")]
    [InlineData("GET_UWSC_NAME")]
    public async Task AssignmentAndShadowingFollowExistingVariableScope(string name)
    {
        List<string> output = [];
        await new Interpreter().RunAsync($$"""
            OPTION EXPLICIT
            {{name}}="changed"
            Change()
            PRINT {{name}}
            Shadow("parameter")
            PRINT {{name}}
            PROCEDURE Change()
                PRINT {{name}}
                {{name}}="routine"
            FEND
            PROCEDURE Shadow({{name}})
                PRINT {{name}}
                DIM {{name}}="local"
                PRINT {{name}}
            FEND
            """, output.Add);
        Assert.Equal(["changed", "routine", "parameter", "local", "routine"], output);
    }

    [Theory]
    [InlineData("GET_WIN_DIR", "PUBLIC")]
    [InlineData("get_win_dir", "CONST")]
    [InlineData("GET_UWSC_DIR", "PUBLIC")]
    [InlineData("get_uwsc_dir", "CONST")]
    [InlineData("GET_UWSC_NAME", "PUBLIC")]
    [InlineData("get_uwsc_name", "CONST")]
    public async Task GlobalRedeclarationFailsBeforeInitializer(
        string name, string declaration)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync($"\n{declaration} {name}=1/0", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Contains(name, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("再定義", error.Message);
    }

    [Fact]
    public async Task ReusedProgramAndConcurrentRunsHaveSeparateSnapshots()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            SLEEP(0.001)
            PRINT GET_WIN_DIR+"|"+GET_UWSC_DIR+"|"+GET_UWSC_NAME
            GET_WIN_DIR="changed"
            GET_UWSC_DIR="changed"
            GET_UWSC_NAME="changed"
            """);
        var runner = new Interpreter();
        var ready = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0;
        void Arrive()
        {
            if (Interlocked.Increment(ref waiting) == 2)
            {
                ready.SetResult();
            }
        }

        async Task<string[]> RunAsync(string name, bool coordinated = true)
        {
            List<string> output = [];
            IScriptHost host = coordinated
                ? new CoordinatedHost(output.Add, Arrive, release.Task)
                : new CallbackScriptHost(output.Add);
            await runner.RunAsync(program, host,
                default, new ScriptRunOptions([])
                {
                    SourcePath = Path.Combine(Path.GetTempPath(), name),
                    WindowsDirectory = name + "-windows",
                    ExecutableDirectory = name + "-app"
                });
            return [.. output];
        }

        Task<string[]> first = RunAsync("first.uws");
        Task<string[]> second = RunAsync("second.uws");
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }

        string[][] results = await Task.WhenAll(first, second);
        Assert.Equal(["first.uws-windows|first.uws-app|first.uws"], results[0]);
        Assert.Equal(["second.uws-windows|second.uws-app|second.uws"], results[1]);
        Assert.Equal(results[0], await RunAsync("first.uws", coordinated: false));
    }

    [Fact]
    public async Task CancellationDoesNotChangeOptionsOrNextRun()
    {
        var options = new ScriptRunOptions([])
        {
            WindowsDirectory = "windows",
            ExecutableDirectory = "app"
        };
        using var cancellation = new CancellationTokenSource();
        var runner = new Interpreter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("""
                GET_WIN_DIR="changed"
                GET_UWSC_DIR="changed"
                GET_UWSC_NAME="changed"
                PRINT 1
                PRINT 2
                """, _ => cancellation.Cancel(), cancellation.Token, options));
        List<string> output = [];
        await runner.RunAsync("""
            PRINT GET_WIN_DIR
            PRINT GET_UWSC_DIR
            PRINT "["+GET_UWSC_NAME+"]"
            """, output.Add, default, options);
        Assert.Equal(["windows", "app", "[]"], output);
        Assert.Equal("windows", options.WindowsDirectory);
        Assert.Equal("app", options.ExecutableDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticAndDynamicCallKeepRootIdentity(bool dynamicCall)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-context-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string main = Path.Combine(directory, "main script.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), """
                PUBLIC initial=GET_UWSC_NAME
                PRINT initial
                PRINT EVAL("GET_UWSC_NAME")
                GET_WIN_DIR="child-windows"
                GET_UWSC_DIR="child-app"
                """);
            await File.WriteAllTextAsync(main,
                "PRINT GET_UWSC_NAME\n" + (dynamicCall ?
                    "EVAL(\"CALL child\")" : "CALL child") + """

                PRINT GET_UWSC_NAME
                PRINT GET_WIN_DIR
                PRINT GET_UWSC_DIR
                """);
            List<string> output = [];
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add), default, new ScriptRunOptions([]));
            Assert.Equal([
                "main script.uws", "main script.uws", "main script.uws",
                "main script.uws", "child-windows", "child-app"
            ], output);
        }
        finally
        {
            // このテストが作成したCALL用フォルダーだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompiledRootIdentityTakesPrecedenceOverRuntimeSourcePath()
    {
        var options = new ScriptRunOptions([])
        {
            SourcePath = Path.Combine(Path.GetTempPath(), "compiled.uws")
        };
        ScriptProgram program = ScriptCompiler.CompileWithContext(
            "PRINT GET_UWSC_NAME", options.SourcePath, true, default);
        List<string> output = [];
        await new Interpreter().RunAsync(program, new CallbackScriptHost(output.Add),
            default, new ScriptRunOptions([])
            {
                SourcePath = Path.Combine(Path.GetTempPath(), "other.uws")
            });
        Assert.Equal(["compiled.uws"], output);
    }


    private sealed class CoordinatedHost(
        Action<string> output, Action arrived, Task release) : IScriptHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            output(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            arrived();
            return release.WaitAsync(token);
        }
    }
}
