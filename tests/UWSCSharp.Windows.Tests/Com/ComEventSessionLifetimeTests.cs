using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComEventSessionLifetimeTests
{
    private sealed class Connection(Runtime owner, int index,
        Exception? failure) : IComEventConnection
    {
        public Exception? TakeCallbackFailure() => null;

        public void Dispose()
        {
            owner.Record("disconnect-" + index);
            owner.OnDispose?.Invoke(index);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class Runtime : IComRuntime
    {
        internal readonly List<string> Events = [];
        internal readonly List<int> Threads = [];
        internal readonly Queue<Exception?> Failures = [];
        internal Exception? ConnectFailure;
        internal CancellationTokenSource? CancelAfterConnect;
        internal Action? OnConnect;
        internal Action<int>? OnDispose;
        private int connections;

        internal void Record(string name)
        {
            Events.Add(name);
            Threads.Add(Environment.CurrentManagedThreadId);
        }

        public object Create(string name)
        {
            Record("create");
            return new object();
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign) => 0;

        public void Release(object target) => Record("release");

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token)
        {
            Record("connect");
            OnConnect?.Invoke();
            if (ConnectFailure is not null)
            {
                throw ConnectFailure;
            }

            CancelAfterConnect?.Cancel();
            return new Connection(this, ++connections,
                Failures.Count > 0 ? Failures.Dequeue() : null);
        }
    }

    [Fact]
    public async Task ScriptCanRegisterAgainAfterAnUnadviseFailure()
    {
        var runtime = new Runtime();
        runtime.Failures.Enqueue(ComOperationErrors.Record(
            new COMException(null, unchecked((int)0x80004005))));
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];

        await new Interpreter(windows: automation).RunAsync("""
            obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"events","changed","Handler")
            TRY
                OLEEVENT(obj)
                PRINT "unexpected"
            EXCEPT
                PRINT "caught"
            ENDTRY
            OLEEVENT(obj,"events","changed","Handler")
            OLEEVENT(obj)
            PRINT "done"
            PROCEDURE Handler()
            FEND
            """, output.Add);

        Assert.Equal(["caught", "done"], output);
        Assert.Equal(["create", "connect", "disconnect-1", "connect",
            "disconnect-2", "release"], runtime.Events);
    }

    [Fact]
    public async Task DisposalAttemptsAllUnadvisesBeforeObjectReleaseOnSameSta()
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var runtime = new Runtime();
        runtime.Failures.Enqueue(first);
        runtime.Failures.Enqueue(second);
        var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        await Connect(session, target);
        await Connect(session, target);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => session.DisposeAsync().AsTask());
        Assert.Equal([second, first], failure.InnerExceptions);
        Assert.Equal(["create", "connect", "connect", "disconnect-2",
            "disconnect-1", "release"], runtime.Events);
        Assert.Single(runtime.Threads.Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRemovalContinuesAndOnlyClassifiesKnownFailures(bool known)
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        if (known)
        {
            ComOperationErrors.Record(first);
            ComOperationErrors.Record(second);
        }

        var runtime = new Runtime();
        runtime.Failures.Enqueue(first);
        runtime.Failures.Enqueue(second);
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        await Connect(session, target);
        await Connect(session, target);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => session.DisconnectEventsAsync(target, default).AsTask());
        Assert.Equal([second, first], failure.InnerExceptions);
        Assert.Equal(known, session.GetScriptErrorMessage(failure) is not null);
        Assert.Equal(0, await session.DisconnectEventsAsync(target, default));
    }

    [Fact]
    public async Task CancellationAfterAdviseStillUnadvisesAtSessionEnd()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { CancelAfterConnect = cancellation };
        var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ConnectEventAsync(target, "events", "changed",
                (ref DISPPARAMS parameters) => { }, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        await session.DisposeAsync();
        Assert.Equal(["create", "connect", "disconnect-1", "release"], runtime.Events);
    }

    [Fact]
    public async Task FailedRegistrationKeepsPreviouslyOwnedConnection()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        await Connect(session, target);
        var failure = new InvalidOperationException("register");
        runtime.ConnectFailure = failure;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Connect(session, target));
        Assert.Same(failure, actual);
        Assert.Equal(1, await session.DisconnectEventsAsync(target, default));
    }

    private static async Task Connect(ComSession session, Value target) =>
        await session.ConnectEventAsync(target, "events", "changed",
            (ref DISPPARAMS parameters) => { }, default);

    [Fact]
    public async Task ReentrantDisconnectDoesNotInvalidateOuterSelection()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        await Connect(session, target);
        await Connect(session, target);
        int nested = -1;
        runtime.OnDispose = index =>
        {
            if (index == 2)
            {
                session.WaitForEventCallback(RemoveAgain());
            }
        };
        Assert.Equal(2, await session.DisconnectEventsAsync(target, default));
        Assert.Equal(0, nested);
        Assert.Equal(["create", "connect", "connect", "disconnect-2", "disconnect-1"],
            runtime.Events);

        async Task RemoveAgain()
        {
            await Task.Yield();
            nested = await session.DisconnectEventsAsync(target, default).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ReentrantRegistrationSurvivesOuterDisconnect()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        await Connect(session, target);
        await Connect(session, target);
        runtime.OnDispose = index =>
        {
            if (index == 2)
            {
                session.WaitForEventCallback(RegisterAgain());
            }
        };
        Assert.Equal(2, await session.DisconnectEventsAsync(target, default));
        Assert.Equal(["create", "connect", "connect", "disconnect-2", "connect",
            "disconnect-1"], runtime.Events);
        Assert.Equal(1, await session.DisconnectEventsAsync(target, default));
        Assert.Equal("disconnect-3", runtime.Events[^1]);

        async Task RegisterAgain()
        {
            await Task.Yield();
            await Connect(session, target).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ScriptRegistrationReplacesSameEventAcrossOwnersCaseInsensitively()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        object firstOwner = new object();
        object secondOwner = new object();
        await session.SetScriptEventAsync(firstOwner, target, "Events", "Changed",
            (values, token) => ValueTask.FromResult(values), default, default);
        await session.SetScriptEventAsync(secondOwner, target, "events", "changed",
            (values, token) => ValueTask.FromResult(values), default, default);
        await session.DisconnectEventOwnerAsync(firstOwner);
        Assert.Equal(["create", "connect", "connect", "disconnect-1"], runtime.Events);
        await session.DisconnectEventOwnerAsync(secondOwner);
        Assert.Equal(["create", "connect", "connect", "disconnect-1", "disconnect-2"],
            runtime.Events);
        Assert.Equal(0, await session.DisconnectEventsAsync(target, default));
    }

    [Fact]
    public async Task PendingScriptReplacementsKeepOnlyTheLastConnection()
    {
        var runtime = new Runtime();
        using var release = new ManualResetEventSlim();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        object oldOwner = new object();
        object firstOwner = new object();
        object lastOwner = new object();
        await Register(oldOwner);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(10);
        runtime.OnConnect = () =>
        {
            runtime.OnConnect = null;
            entered.SetResult();
            if (!release.Wait(timeout))
            {
                throw new TimeoutException("先行する接続の再開を確認できませんでした。");
            }
        };

        Task first = Register(firstOwner).AsTask();
        Task last;
        try
        {
            await entered.Task.WaitAsync(timeout);
            last = Register(lastOwner).AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(last.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(first, last).WaitAsync(timeout);
        await session.DisconnectEventOwnerAsync(oldOwner);
        await session.DisconnectEventOwnerAsync(firstOwner);
        Assert.Equal(["create", "connect", "connect", "disconnect-1",
            "connect", "disconnect-2"], runtime.Events);
        await session.DisconnectEventOwnerAsync(lastOwner);
        Assert.Equal("disconnect-3", runtime.Events[^1]);
        Assert.Equal(0, await session.DisconnectEventsAsync(target, default));

        ValueTask Register(object owner) => session.SetScriptEventAsync(owner, target,
            "Events", "Changed", (arguments, _) => ValueTask.FromResult(arguments),
            default, default);
    }

    [Fact]
    public async Task FailedScriptReplacementPreservesPreviousOwnerRegistration()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        object owner = new object();
        await session.SetScriptEventAsync(owner, target, "events", "changed",
            (values, token) => ValueTask.FromResult(values), default, default);
        var failure = new InvalidOperationException("connect");
        runtime.ConnectFailure = failure;
        object nextOwner = new object();
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SetScriptEventAsync(nextOwner, target, "events", "changed",
                (values, token) => ValueTask.FromResult(values), default, default).AsTask());
        Assert.Same(failure, actual);
        await session.DisconnectEventOwnerAsync(nextOwner);
        Assert.Equal(["create", "connect", "connect"], runtime.Events);

        runtime.ConnectFailure = null;
        await session.SetScriptEventAsync(nextOwner, target, "events", "changed",
            (values, token) => ValueTask.FromResult(values), default, default);
        await session.DisconnectEventOwnerAsync(owner);
        Assert.Equal(["create", "connect", "connect", "connect", "disconnect-1"],
            runtime.Events);

        await session.DisconnectEventOwnerAsync(nextOwner);
        Assert.Equal(["create", "connect", "connect", "connect", "disconnect-1",
            "disconnect-2"], runtime.Events);
    }

    [Fact]
    public async Task CancelledScriptRegistrationStillBelongsToItsOwner()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { CancelAfterConnect = cancellation };
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        object owner = new object();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SetScriptEventAsync(owner, target, "events", "changed",
                (values, token) => ValueTask.FromResult(values), default,
                cancellation.Token).AsTask());
        await session.DisconnectEventOwnerAsync(owner);
        Assert.Equal(["create", "connect", "disconnect-1"], runtime.Events);
    }
}
