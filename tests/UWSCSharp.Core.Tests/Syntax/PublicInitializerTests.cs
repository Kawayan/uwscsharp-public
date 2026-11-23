using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PublicInitializerTests
{
    [Theory]
    [InlineData("PUBLIC a=2\nPUBLIC b=a+3\nPRINT b", "5")]
    [InlineData("PUBLIC a=ABS(-7)\nPRINT a", "7")]
    [InlineData("CONST unit=2\nPUBLIC amount=unit+1\nPRINT amount", "3")]
    [InlineData("PUBLIC count=LENGTH(PARAM_STR)\nPRINT count", "1")]
    [InlineData("PUBLIC items=SPLIT(\"a,b\",\",\")\nPRINT LENGTH(items)", "2")]
    public async Task InitializesFromEarlierGlobalsAndBuiltinCalls(string source,
        string expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(source, output.Add, default,
            new ScriptRunOptions(["argument"]));
        Assert.Equal([expected], output);
    }

    [Theory]
    [InlineData("PUBLIC value=missing")]
    [InlineData("PUBLIC value=value")]
    [InlineData("PUBLIC value=later\nPUBLIC later=2")]
    [InlineData("PUBLIC value=ABS()")]
    [InlineData("PUBLIC value=1/0")]
    public async Task InitializerFailurePreventsMainAndFinally(string declaration)
    {
        List<string> output = [];
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(declaration
                + "\nTRY\nPRINT 1\nFINALLY\nPRINT 2\nENDTRY", output.Add));
        Assert.Equal(1, error.Line);
        Assert.Empty(output);
    }

    [Fact]
    public async Task RoutineDefaultReadsCurrentPublicValue()
    {
        List<string> output = [];
        await new Interpreter().RunAsync(
            "PUBLIC value=3\nP()\nvalue=7\nP()\n"
            + "PROCEDURE P(arg=value)\nPRINT arg\nFEND", output.Add);
        Assert.Equal(["3", "7"], output);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("script-error")]
    [InlineData("cancel")]
    public async Task BuiltinInitializerReleasesSessionOnEveryCompletion(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var files = new InitializerFiles(mode == "cancel" ? cancellation : null);
        string source = "PUBLIC id=FOPEN(\"unused\",F_READ)\n"
            + (mode == "script-error" ? "PUBLIC later=missing\n" : "")
            + "PRINT id";
        List<string> output = [];
        Task execution = new Interpreter(files: files).RunAsync(source,
            output.Add, cancellation.Token);
        if (mode == "script-error")
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => execution);
            Assert.Equal(2, error.Line);
            Assert.Empty(output);
        }
        else if (mode == "cancel")
        {
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(output);
        }
        else
        {
            await execution;
            Assert.Equal(["17"], output);
        }

        Assert.Equal(1, files.Calls);
        Assert.Equal(1, files.Disposals);
    }

    private sealed class InitializerFiles(CancellationTokenSource? cancellation)
        : IScriptFiles, IScriptFileSession
    {
        internal int Calls
        {
            get; private set;
        }

        internal int Disposals
        {
            get; private set;
        }

        public IScriptFileSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Calls++;
            cancellation?.Cancel();
            return ValueTask.FromResult(Value.Numeric(17));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
