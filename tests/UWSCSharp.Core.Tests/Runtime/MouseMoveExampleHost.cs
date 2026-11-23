using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

internal sealed class MouseMoveExampleHost
    : IWindowsAutomation, IWindowsAutomationSession, IScriptHost
{
    internal List<(int X, int Y)> Positions { get; } = [];

    internal List<TimeSpan> Delays { get; } = [];

    internal List<string> Output { get; } = [];

    internal int Disposals
    {
        get; private set;
    }

    public IWindowsAutomationSession CreateSession() => this;

    public ValueTask<Value> InvokeAsync(string name,
        IReadOnlyList<Value> arguments, CancellationToken token)
    {
        Assert.Equal("MMV", name);
        Positions.Add(((int)arguments[0].Number, (int)arguments[1].Number));
        return ValueTask.FromResult(Value.Empty);
    }

    public ValueTask WriteOutputAsync(string text, CancellationToken token)
    {
        Output.Add(text);
        return ValueTask.CompletedTask;
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken token)
    {
        Delays.Add(duration);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}
