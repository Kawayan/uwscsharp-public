using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComEventReentrancyTests
{
    private sealed class Runtime : IComRuntime
    {
        internal Func<string, object?>? Handler;
        internal readonly List<int> Threads = [];
        internal int Releases;

        public object Create(string name) => new object();

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return Handler!(name);
        }

        public void Release(object target) => Interlocked.Increment(ref Releases);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AsynchronousCallbackCanCallSameStaRecursively(int depth)
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        int notifications = 0;
        runtime.Handler = name =>
        {
            int current = int.Parse(name);
            if (current > 0)
            {
                session.WaitForEventCallback(Reenter(current - 1));
                notifications++;
            }

            return current;
        };

        Value result = await session.InvokeAsync(target, depth.ToString(),
            [], null, default);
        Assert.Equal(depth, result.AsNumber());
        Assert.Equal(depth, notifications);
        Assert.Equal(depth + 1, runtime.Threads.Count);
        Assert.Single(runtime.Threads.Distinct());

        async Task Reenter(int current)
        {
            await Task.Yield();
            Value nested = await session.InvokeAsync(target, current.ToString(),
                [], null, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(current, nested.AsNumber());
        }
    }

    [Fact]
    public async Task CallbackFailurePreservesOriginalException()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        var expected = new InvalidOperationException("callback");
        runtime.Handler = _ =>
        {
            session.WaitForEventCallback(Fail());
            return 0;
        };

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.InvokeAsync(target, "fire", [], null, default).AsTask());
        Assert.Same(expected, actual);
        Assert.Null(session.GetScriptErrorMessage(actual));

        async Task Fail()
        {
            await Task.Yield();
            throw expected;
        }
    }

    [Fact]
    public async Task CancelledNestedOperationKeepsItsToken()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        runtime.Handler = _ =>
        {
            session.WaitForEventCallback(Reenter());
            return 0;
        };

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.InvokeAsync(target, "fire", [], null, default).AsTask());
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Single(runtime.Threads);

        async Task Reenter()
        {
            await Task.Yield();
            await session.InvokeAsync(target, "cancelled", [], null, cancellation.Token);
        }
    }

    [Fact]
    public async Task ShutdownWaitsForBorrowedCallbackBeforeReleasingTarget()
    {
        var runtime = new Runtime();
        var session = new ComSession(runtime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? operation = null;
        try
        {
            Value target = await session.CreateAsync("fixture", false, default);
            runtime.Handler = _ =>
            {
                entered.SetResult();
                session.WaitForEventCallback(finish.Task);
                return 0;
            };
            operation = session.InvokeAsync(target, "fire", [], null, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task disposal = session.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref runtime.Releases));
            finish.SetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, runtime.Releases);
        }
        finally
        {
            finish.TrySetResult();
            if (operation is not null)
            {
                await operation;
            }

            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonOwnerThreadCannotPumpComSession()
    {
        await using var session = new ComSession(new Runtime());
        Assert.Throws<InvalidOperationException>(
            () => session.WaitForEventCallback(Task.CompletedTask));
    }
}
