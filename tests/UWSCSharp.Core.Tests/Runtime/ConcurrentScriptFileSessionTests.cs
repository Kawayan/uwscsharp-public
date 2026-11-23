using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ConcurrentScriptFileSessionTests
{
    [Fact]
    public async Task ConcurrentCallsKeepTheirOwnResultAndDirectorySnapshot()
    {
        TaskCompletionSource<bool> entered = Signal();
        TaskCompletionSource<bool> release = Signal();
        var session = new FileSession();
        session.Handler = async (arguments, token) =>
        {
            if (arguments[0].Number == 1)
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }

            session.Names.Clear();
            session.Names.Add(arguments[0].Number.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            return arguments[0];
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        Task<ScriptFileCallResult> first = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.GetDir, [Value.Numeric(1)], default).AsTask();
        await entered.Task;
        Task<ScriptFileCallResult> second = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.GetDir, [Value.Numeric(2)], default).AsTask();
        Assert.Equal(1, session.Calls);
        Assert.False(second.IsCompleted);
        release.TrySetResult(true);

        ScriptFileCallResult firstResult = await first;
        ScriptFileCallResult secondResult = await second;
        Assert.Equal(Value.Numeric(1), firstResult.Value);
        Assert.Equal(["1"], firstResult.DirectoryFiles);
        Assert.Equal(Value.Numeric(2), secondResult.Value);
        Assert.Equal(["2"], secondResult.DirectoryFiles);
        session.Names[0] = "later";
        Assert.Equal(["1"], firstResult.DirectoryFiles);
        Assert.Equal(["2"], secondResult.DirectoryFiles);
        Assert.Equal(1, session.MaximumActive);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task ReturnedSnapshotRejectsMutation()
    {
        var session = new FileSession();
        session.Names.Add("before");
        var concurrent = new ConcurrentScriptFileSession(session);
        ScriptFileCallResult result = await concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.GetDir, [], default);
        IList<string> list = Assert.IsAssignableFrom<IList<string>>(
            result.DirectoryFiles);
        Assert.Throws<NotSupportedException>(() => list[0] = "changed");
        Assert.Equal(["before"], session.Names);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotInvokeOrReleaseAnotherCallsGate()
    {
        TaskCompletionSource<bool> entered = Signal();
        TaskCompletionSource<bool> release = Signal();
        var session = new FileSession
        {
            Handler = async (_, token) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return Value.Numeric(7);
            }
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        Task<ScriptFileCallResult> first = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], default).AsTask();
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        Task<ScriptFileCallResult> cancelled = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], cancellation.Token).AsTask();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, session.Calls);
        Task<ScriptFileCallResult> third = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], default).AsTask();
        Assert.False(third.IsCompleted);
        release.TrySetResult(true);
        await first;
        await third;
        Assert.Equal(2, session.Calls);
        Assert.Equal(1, session.MaximumActive);
    }

    [Fact]
    public async Task BackendCancellationReleasesGateAndKeepsOriginalToken()
    {
        TaskCompletionSource<bool> entered = Signal();
        TaskCompletionSource<bool> never = Signal();
        var session = new FileSession
        {
            Handler = async (_, token) =>
            {
                entered.TrySetResult(true);
                await never.Task.WaitAsync(token);
                return Value.Empty;
            }
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        using var cancellation = new CancellationTokenSource();
        Task<ScriptFileCallResult> call = concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        session.Handler = (_, _) => ValueTask.FromResult(Value.Numeric(9));
        Assert.Equal(Value.Numeric(9), (await concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], default)).Value);
    }

    [Fact]
    public async Task InvocationFailureKeepsExceptionAndReleasesGate()
    {
        var expected = new IOException("ファイル読取り失敗");
        var session = new FileSession
        {
            Handler = (_, _) => throw expected
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            concurrent.InvokeWithSnapshotAsync(
                BuiltinFunctionNames.FGet, [], default).AsTask());
        Assert.Same(expected, actual);
        session.Handler = (_, _) => ValueTask.FromResult(Value.Numeric(3));
        Assert.Equal(Value.Numeric(3), (await concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], default)).Value);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task SnapshotFailureReleasesGateWithoutReturningPartialResult()
    {
        var expected = new IOException("名前一覧の取得失敗");
        var session = new FileSession
        {
            SnapshotFailure = expected
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            concurrent.InvokeWithSnapshotAsync(
                BuiltinFunctionNames.GetDir, [], default).AsTask());
        Assert.Same(expected, actual);
        session.SnapshotFailure = null;
        session.Names.Add("recovered");
        ScriptFileCallResult result = await concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.GetDir, [], default);
        Assert.Equal(["recovered"], result.DirectoryFiles);
    }

    [Fact]
    public async Task CancellationAfterSideEffectDoesNotPublishAResult()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FileSession
        {
            Handler = (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(Value.Numeric(5));
            }
        };
        var concurrent = new ConcurrentScriptFileSession(session);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => concurrent.InvokeWithSnapshotAsync(
                BuiltinFunctionNames.FGet, [], cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, session.Calls);
        session.Handler = (_, _) => ValueTask.FromResult(Value.Numeric(6));
        Assert.Equal(Value.Numeric(6), (await concurrent.InvokeWithSnapshotAsync(
            BuiltinFunctionNames.FGet, [], default)).Value);
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FileSession : IScriptFileSession
    {
        private int active;

        internal List<string> Names
        {
            get;
        } = [];

        internal Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<Value>> Handler
        {
            get;
            set;
        } =
            (_, _) => ValueTask.FromResult(Value.Empty);

        internal Exception? SnapshotFailure
        {
            get;
            set;
        }

        internal int Calls
        {
            get;
            private set;
        }

        internal int MaximumActive
        {
            get;
            private set;
        }

        internal bool Disposed
        {
            get;
            private set;
        }

        public IReadOnlyList<string> DirectoryFiles
        {
            get
            {
                if (SnapshotFailure is not null)
                {
                    throw SnapshotFailure;
                }

                return Names;
            }
        }

        public async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Assert.True(FileFunctions.IsBuiltin(name));
            Calls++;
            int count = Interlocked.Increment(ref active);
            MaximumActive = Math.Max(MaximumActive, count);
            try
            {
                return await Handler(arguments, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
