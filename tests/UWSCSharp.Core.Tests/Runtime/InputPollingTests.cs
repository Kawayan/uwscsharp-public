using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputPollingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturesReleasedInputDuringSleepOrWindowsWait(bool windowsWait)
    {
        var adapter = new Adapter();
        var host = new Host(adapter);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string source = (windowsWait ? "GETID(\"owned\")" : "SLEEP(10)")
            + "\nPRINT GETKEYSTATE(1)\nPRINT GETKEYSTATE(1)";
        await new Interpreter(windows: adapter).RunAsync(source, host, deadline.Token);
        Assert.Equal(["1", "0"], host.Output);
        Assert.True(adapter.Polls > 0);
        Assert.Equal(0, adapter.Pressed);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task PollingFailureWaitsForAlreadyStartedHostOperation()
    {
        var adapter = new Adapter();
        var host = new Host(adapter)
        {
            FailDuringWait = true
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = new Interpreter(windows: adapter)
            .RunAsync("SLEEP(10)", host, deadline.Token);
        await adapter.PollFailed.Task.WaitAsync(deadline.Token);
        Assert.False(running.IsCompleted);
        Assert.False(adapter.Disposed);
        host.Release.TrySetResult();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => running.WaitAsync(deadline.Token));
        Assert.Same(adapter.Failure, failure);
        Assert.True(adapter.Disposed);
    }

    private sealed class Host(Adapter adapter) : IScriptHost
    {
        internal List<string> Output { get; } = [];

        internal bool FailDuringWait
        {
            get; init;
        }

        internal TaskCompletionSource Release
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (FailDuringWait)
            {
                adapter.FailPolling = true;
                await Release.Task.WaitAsync(token);
            }
            else
            {
                await adapter.PressUntilPolledAsync(token);
            }
        }
    }

    private sealed class Adapter : IWindowsAutomation,
        IWindowsAutomationSession
    {
        private int pending;

        internal int Pressed;
        internal int Polls;
        internal bool FailPolling;
        internal bool Disposed;
        internal InvalidOperationException Failure { get; } = new("poll failure");

        internal TaskCompletionSource Captured
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource PollFailed
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RequiresInputPolling => true;

        public IWindowsAutomationSession CreateSession() => this;

        public void PollInput(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Polls);
            if (FailPolling)
            {
                PollFailed.TrySetResult();
                throw Failure;
            }

            if (Volatile.Read(ref Pressed) != 0)
            {
                Interlocked.Exchange(ref pending, 1);
                Captured.TrySetResult();
            }
        }

        internal async Task PressUntilPolledAsync(CancellationToken token)
        {
            Volatile.Write(ref Pressed, 1);
            await Captured.Task.WaitAsync(token);
            Volatile.Write(ref Pressed, 0);
        }

        public async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            if (name == "GETID")
            {
                await PressUntilPolledAsync(token);
                return Value.Numeric(7);
            }

            return Value.Numeric(Interlocked.Exchange(ref pending, 0));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
