using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowPositionForwardingTests
{
    [Fact]
    public async Task NestedLoggingPreservesTheRequestTokenAndCompletion()
    {
        var target = new Host();
        ScriptLogHost host = Wrap(Wrap(target));
        var request = new ScriptWindowPosition(-80, 240);
        using var cancellation = new CancellationTokenSource();
        Assert.True(host.SupportsWindowPosition);
        Task pending = host.SetWindowPositionAsync(request,
            cancellation.Token).AsTask();
        Assert.Same(request, target.Request);
        Assert.Equal(cancellation.Token, target.Token);
        Assert.False(pending.IsCompleted);
        target.Completion.SetResult();
        await pending;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedTargetsAreNotCalled(bool hasInterface)
    {
        var target = new Host { SupportsWindowPosition = false };
        ScriptLogHost host = Wrap(hasInterface
            ? target : new CallbackScriptHost(_ => { }));
        Assert.False(host.SupportsWindowPosition);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            host.SetWindowPositionAsync(new(1, 2), default).AsTask());
        Assert.Null(target.Request);
    }

    [Fact]
    public async Task TargetFailureAndCancellationAreNotSwallowed()
    {
        var target = new Host();
        var failure = new InvalidOperationException("配置失敗");
        Task pending = Wrap(target).SetWindowPositionAsync(new(1, 2),
            default).AsTask();
        target.Completion.SetException(failure);
        Assert.Same(failure,
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending));

        target = new Host();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        pending = Wrap(target).SetWindowPositionAsync(new(1, 2),
            cancellation.Token).AsTask();
        target.Completion.SetCanceled(cancellation.Token);
        OperationCanceledException cancelled =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
    }

    private static ScriptLogHost Wrap(IScriptHost target) =>
        new(target, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));

    private sealed class Host : IScriptWindowPositionHost
    {
        internal ScriptWindowPosition? Request;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SupportsWindowPosition { get; init; } = true;

        public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Token = cancellationToken;
            return new(Completion.Task);
        }

        public ValueTask WriteOutputAsync(string text,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public Task DelayAsync(TimeSpan duration,
            CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
    }
}
