using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class HostBoundaryTests
{
    [Theory]
    [InlineData("PRINT 1", false, false)]
    [InlineData("PRINT 1", true, false)]
    [InlineData("PRINT 1", false, true)]
    [InlineData("PRINT 1", true, true)]
    [InlineData("SLEEP(0)", false, false)]
    [InlineData("SLEEP(0)", true, false)]
    [InlineData("SLEEP(0)", false, true)]
    [InlineData("SLEEP(0)", true, true)]
    [InlineData("FOPEN(\"unused\")", false, false)]
    [InlineData("FOPEN(\"unused\")", true, false)]
    [InlineData("FOPEN(\"unused\")", false, true)]
    [InlineData("FOPEN(\"unused\")", true, true)]
    public async Task ExternalFailureBypassesExceptAndKeepsIdentity(
        string statement, bool asynchronous, bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancelled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("external failure");
        var services = new FailingServices(failure, asynchronous);
        string script = $"TRY\n{statement}\nEXCEPT\nPRINT 2\nENDTRY";

        Exception actual = await Record.ExceptionAsync(() =>
            new Interpreter(files: services).RunAsync(script,
                services, cancellation.Token));

        Assert.Same(failure, actual);
        Assert.Equal(1, services.Calls);
        Assert.True(services.Disposed);
    }

    private sealed class FailingServices(Exception failure, bool asynchronous)
        : IScriptHost, IScriptFiles, IScriptFileSession
    {
        internal int Calls
        {
            get; private set;
        }

        internal bool Disposed
        {
            get; private set;
        }

        public IScriptFileSession CreateSession() => this;

        public ValueTask WriteOutputAsync(string text,
            CancellationToken cancellationToken) => FailAsync();

        public Task DelayAsync(TimeSpan duration,
            CancellationToken cancellationToken) => FailAsync().AsTask();

        public async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            await FailAsync();
            return Value.Empty;
        }

        private ValueTask FailAsync()
        {
            Calls++;
            if (!asynchronous)
            {
                throw failure;
            }

            return FailAfterYieldAsync();
        }

        private async ValueTask FailAfterYieldAsync()
        {
            await Task.Yield();
            throw failure;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
