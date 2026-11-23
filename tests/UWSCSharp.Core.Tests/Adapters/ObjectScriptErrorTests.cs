using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ObjectScriptErrorTests
{
    private sealed class Adapter(Exception failure, bool reports)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Mapped;
        internal Exception? MappingFailure;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken cancellationToken) => throw failure;

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(
            Value target, string name, IReadOnlyList<WindowsMemberArgument> arguments,
            Value? assigned, CancellationToken cancellationToken) => throw failure;

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(
            Value target, CancellationToken cancellationToken) => throw failure;

        public string? GetObjectScriptErrorMessage(Exception exception)
        {
            Assert.Same(failure, exception);
            Mapped++;
            if (MappingFailure is not null)
            {
                throw MappingFailure;
            }

            return reports ? "object操作失敗" : null;
        }
    }

    [Theory]
    [InlineData("PRINT o.Count", true)]
    [InlineData("o.Count=1", true)]
    [InlineData("o.Change(VAR a)", true)]
    [InlineData("PRINT LENGTH(o)", true)]
    [InlineData("FOR x IN o\nPRINT x\nNEXT", true)]
    [InlineData("PRINT o.Count", false)]
    [InlineData("o.Change(VAR a)", false)]
    [InlineData("PRINT LENGTH(o)", false)]
    [InlineData("FOR x IN o\nPRINT x\nNEXT", false)]
    public async Task ObjectFailuresRequireAdapterOptIn(string call, bool reports)
    {
        var failure = new InvalidOperationException("internal failure");
        var adapter = new Adapter(failure, reports);
        List<string> output = [];
        string source = "o=CREATEOLEOBJ(\"fixture\")\na=7\nTRY\n" + call
            + "\nEXCEPT\nPRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\n"
            + "ENDTRY\nPRINT a";
        if (reports)
        {
            await new Interpreter(windows: adapter).RunAsync(source, output.Add);
            Assert.Equal("4", output[0]);
            Assert.Contains("object操作失敗", output[1]);
            Assert.Equal("7", output[2]);
            Assert.Equal(3, output.Count);
        }
        else
        {
            InvalidOperationException actual =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new Interpreter(windows: adapter).RunAsync(source, output.Add));
            Assert.Same(failure, actual);
            Assert.Empty(output);
        }

        Assert.Equal(1, adapter.Mapped);
    }

    [Fact]
    public async Task ObjectReporterFailureRemainsHostFailure()
    {
        var failure = new InvalidOperationException("operation");
        var reportFailure = new FormatException("reporter");
        var adapter = new Adapter(failure, true)
        {
            MappingFailure = reportFailure
        };
        List<string> output = [];
        FormatException actual = await Assert.ThrowsAsync<FormatException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nTRY\nPRINT o.Count\n"
                + "EXCEPT\nPRINT \"caught\"\nENDTRY", output.Add));
        Assert.Same(reportFailure, actual);
        Assert.Empty(output);
    }

    [Fact]
    public async Task ObjectCancellationBypassesReporter()
    {
        using var cancel = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancel.Token);
        var adapter = new Adapter(failure, true);
        OperationCanceledException actual =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new Interpreter(windows: adapter).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nPRINT o.Count",
                    _ => { }, cancel.Token));
        Assert.Equal(cancel.Token, actual.CancellationToken);
        Assert.Equal(0, adapter.Mapped);
    }

    [Fact]
    public async Task UncaughtMappedFailureKeepsOriginalCause()
    {
        var failure = new InvalidOperationException("operation");
        var adapter = new Adapter(failure, true);
        ScriptException actual = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nPRINT o.Count", _ => { }));
        Assert.Equal(2, actual.Line);
        Assert.Same(failure, actual.InnerException?.InnerException);
    }
}
