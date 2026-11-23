using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class FileOperationTests
{
    private sealed class Files(Exception? disposeFailure = null) : IScriptFiles
    {
        internal List<Session> Sessions { get; } = [];

        public IScriptFileSession CreateSession()
        {
            var session = new Session(disposeFailure);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class Session(Exception? disposeFailure) : IScriptFileSession
    {
        internal List<(string Name, Value[] Arguments)> Calls { get; } = [];

        internal bool Disposed
        {
            get; private set;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add((name, arguments.ToArray()));
            return ValueTask.FromResult(Value.Numeric(7));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return disposeFailure is null ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeFailure);
        }
    }

    private sealed class Windows : IWindowsAutomation,
        IWindowsAutomationSession
    {
        internal bool Disposed
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(new Value(ValueKind.Empty));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("FOPEN()")]
    [InlineData("FOPEN(\"x\", F_EXCLUSIVE)")]
    [InlineData("FOPEN(\"x\", F_WRITE8 OR F_WRITE16)")]
    [InlineData("FOPEN(\"x\", F_EXISTS OR F_READ)")]
    [InlineData("FOPEN(\"x\", 0)")]
    [InlineData("FOPEN(\"x\", 1024)")]
    [InlineData("FCLOSE()")]
    [InlineData("FGET(1, 0)")]
    [InlineData("FGET(1, -3)")]
    [InlineData("FGET(1, 1, -1)")]
    [InlineData("FGET(1, 1, 1, 3)")]
    [InlineData("FPUT(1)")]
    [InlineData("FPUT(1, \"x\", -1)")]
    [InlineData("FPUT(1, \"x\", 1, -2)")]
    [InlineData("FPUT(1, \"x\", 0, F_INSERT)")]
    [InlineData("FPUT(1, \"x\", 1, 1, 4)")]
    [InlineData("FPUT(1, \"x\", 1, 1, -1)")]
    [InlineData("FPUT(1, \"x\", 1, 1, 2, 0)")]
    [InlineData("FDELLINE(1, 0)")]
    [InlineData("FDELLINE(1, -1)")]
    [InlineData("FDELLINE(1)")]
    [InlineData("READINI(\"s\")")]
    [InlineData("READINI(\"\", \"x\")")]
    [InlineData("WRITEINI(\"s\", \"x\")")]
    [InlineData("WRITEINI(\"s\", \"bad=key\", 1)")]
    [InlineData("WRITEINI(\"[s]\", \"key\", 1)")]
    [InlineData("DELETEINI()")]
    [InlineData("GETDIR()")]
    [InlineData("GETDIR(\"x\", \"*\", , 5)")]
    [InlineData("GETDIR(\"x\", \"sub/*.txt\")")]
    [InlineData("DELETEFILE()")]
    public async Task RejectsUnsupportedModesBeforeAdapterCall(string source)
    {
        var files = new Files();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter(files: files).RunAsync(
                "\n" + source, _ => { }));
        Assert.Equal(2, error.Line);
        Session session = Assert.Single(files.Sessions);
        Assert.Empty(session.Calls);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task FileIdsConstantsAndOmittedArgumentsReachAdapter()
    {
        var files = new Files();
        await new Interpreter(files: files).RunAsync("""
            id=fopen("x", F_WRITE8B OR F_NOCR)
            FPUT(id, "value", , 0)
            FGET(id, F_LINECOUNT)
            FCLOSE(id, TRUE)
            """, _ => { });
        Session session = Assert.Single(files.Sessions);
        Assert.Equal(["FOPEN", "FPUT", "FGET", "FCLOSE"],
            session.Calls.Select(call => call.Name));
        Assert.Equal(Value.Numeric(160),
            session.Calls[0].Arguments[1]);
        Assert.Equal(Value.Numeric(7), session.Calls[1].Arguments[0]);
        Assert.Equal(ValueKind.Omitted,
            session.Calls[1].Arguments[2].Kind);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task NoAdapterReportsLineAndStaticParsingNeverOpensFiles()
    {
        _ = ScriptCompiler.Compile("FOPEN(\"x\", F_WRITE)");
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("FOPEN(\"x\")"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter().RunAsync("\nFOPEN(\"x\")", _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task FileCloseFailureStillDisposesWindowsInputSession()
    {
        var expected = new IOException("close failed");
        var files = new Files(expected);
        var windows = new Windows();
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: windows, files: files)
                .RunAsync("PRINT 1", _ => { }));
        Assert.Same(expected, error);
        Assert.True(Assert.Single(files.Sessions).Disposed);
        Assert.True(windows.Disposed);
    }

    [Fact]
    public async Task ExclusiveAndCsvModeReachAdapterWithoutLosingArguments()
    {
        var files = new Files();
        await new Interpreter(files: files).RunAsync("""
            id=FOPEN("x", F_READ OR F_WRITE8 OR F_EXCLUSIVE)
            FPUT(id, "value", 2, 4, 2)
            FPUT(id, "raw", 2, 1, 3)
            """, _ => { });
        Session session = Assert.Single(files.Sessions);
        Assert.Equal(Value.Numeric(530), session.Calls[0].Arguments[1]);
        Assert.Equal(Value.Numeric(4), session.Calls[1].Arguments[3]);
        Assert.Equal(Value.Numeric(2), session.Calls[1].Arguments[4]);
        Assert.Equal(Value.Numeric(3), session.Calls[2].Arguments[4]);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task IniAndDirectoryStringArgumentsAreNotTreatedAsIntegers()
    {
        var files = new Files();
        await new Interpreter(files: files).RunAsync("""
            READINI("section", "key", "settings.ini")
            WRITEINI("section", "key", "value", "settings.ini")
            DELETEINI("section", , "settings.ini")
            GETDIR("folder", "*.txt", TRUE, 2)
            DELETEFILE("folder/*.txt")
            FDELLINE(7, 1)
            """, _ => { });
        Session session = Assert.Single(files.Sessions);
        Assert.Equal(["READINI", "WRITEINI", "DELETEINI", "GETDIR",
            "DELETEFILE", "FDELLINE"], session.Calls.Select(call => call.Name));
        Assert.Equal(Value.String("value"), session.Calls[1].Arguments[2]);
        Assert.Equal(ValueKind.Omitted, session.Calls[2].Arguments[1].Kind);
        Assert.Equal(Value.Numeric(2), session.Calls[3].Arguments[3]);
        Assert.True(session.Disposed);
    }
}
