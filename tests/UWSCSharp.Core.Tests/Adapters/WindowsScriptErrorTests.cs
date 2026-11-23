using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class WindowsScriptErrorTests
{
    private sealed class Adapter(Exception failure, bool reports)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Mapped;
        internal Exception? MappingFailure;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) => throw failure;

        public string? GetScriptErrorMessage(string name, Exception exception)
        {
            Assert.Same(failure, exception);
            Mapped++;
            if (MappingFailure is not null)
            {
                throw MappingFailure;
            }

            return reports ? "操作失敗" : null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterChoosesScriptErrorOrHostFailure(bool reports)
    {
        var failure = new InvalidOperationException("adapter failure");
        var adapter = new Adapter(failure, reports);
        var runner = new Interpreter(windows: adapter);
        List<string> output = [];
        const string source = "TRY\nCLKITEM(1, \"name\", 64)\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\nENDTRY\nPRINT \"after\"";
        if (reports)
        {
            await runner.RunAsync(source, output.Add, default);
            Assert.Equal("2", output[0]);
            Assert.Contains("操作失敗", output[1]);
            Assert.Equal("after", output[2]);
            Assert.Equal(3, output.Count);
        }
        else
        {
            InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RunAsync(source, output.Add, default));
            Assert.Same(failure, actual);
            Assert.Empty(output);
        }

        Assert.Equal(1, adapter.Mapped);
    }

    [Fact]
    public async Task ErrorReporterFailureRemainsHostFailure()
    {
        var failure = new InvalidOperationException("操作失敗");
        var reportFailure = new InvalidOperationException("分類失敗");
        var adapter = new Adapter(failure, true) { MappingFailure = reportFailure };
        List<string> output = [];
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "TRY\nCLKITEM(1, \"name\", 64)\nEXCEPT\nPRINT 1\nENDTRY",
                output.Add, default));
        Assert.Same(reportFailure, actual);
        Assert.Empty(output);
    }

    [Fact]
    public async Task CancellationDoesNotAskAdapterToReportScriptError()
    {
        var failure = new OperationCanceledException();
        var adapter = new Adapter(failure, true);
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "TRY\nCLKITEM(1, \"name\", 64)\nEXCEPT\nPRINT 1\nENDTRY",
                output.Add, default));
        Assert.Equal(0, adapter.Mapped);
        Assert.Empty(output);
    }
}
