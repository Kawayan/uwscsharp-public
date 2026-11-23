using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// STA threadは使いますが、COM初期化・pump・機器・権限要求は偽APIへ送ります。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InitializationOperationsAndDisposalUseTheSameSta(int initializeResult)
    {
        var trace = new Trace();
        var apartment = new Apartment(trace) { InitializeResult = initializeResult };
        var backend = new Backend(trace);
        await using var session = new SensorSession(() =>
        {
            trace.Record("create");
            return backend;
        }, apartment);
        SensorQueryResult result = await session.QueryAsync(Field());
        Assert.Equal(0, result.DiscoveryResult);
        Assert.True(result.HasValue);
        Assert.Equal(12.5d, result.Value);
        await session.DisposeAsync();
        string[] operations = [.. trace.Calls.Select(call => call.Operation).Where(value => value != "pump")];
        Assert.Equal(["initialize", "create", "refresh", "read", "dispose", "uninitialize"], operations);
        Assert.Single(trace.Calls.Select(call => call.Thread).Distinct());
        Assert.All(trace.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
    }

    [Fact]
    public async Task QueryKeepsDiscoveryFailureAndMissingValueDistinct()
    {
        var trace = new Trace();
        var backend = new Backend(trace)
        {
            DiscoveryResult = unchecked((int)0x80040154),
            Found = false,
            Value = "未採用の値"
        };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        SensorQueryResult result = await session.QueryAsync(Field());
        Assert.Equal(backend.DiscoveryResult, result.DiscoveryResult);
        Assert.False(result.HasValue);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("value")]
    [InlineData(23L)]
    [InlineData(true)]
    public async Task CopiedPrimitiveValuesCanCrossTheSessionBoundary(object value)
    {
        var trace = new Trace();
        var backend = new Backend(trace) { Value = value };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        Assert.Equal(value, (await session.QueryAsync(Field())).Value);
    }

    [Fact]
    public async Task InterfacesAndRawPointersCannotEscapeAsValues()
    {
        var trace = new Trace();
        var backend = new Backend(trace) { Value = new nint(123) };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.QueryAsync(Field()));
        backend.Value = new object();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.QueryAsync(Field()));
        backend.Value = double.NaN;
        Assert.True(double.IsNaN(Assert.IsType<double>((await session.QueryAsync(Field())).Value)));
    }

    [Fact]
    public async Task QueuedCancellationDoesNotRunTheCanceledRequest()
    {
        var trace = new Trace();
        using var release = new ManualResetEventSlim();
        TaskCompletionSource started = Signal();
        var backend = new Backend(trace) { OnRefresh = () => Block(started, release) };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        Task<SensorQueryResult> first = session.QueryAsync(Field()).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            Task<SensorQueryResult> pending = session.QueryAsync(Field(), cancellation.Token).AsTask();
            cancellation.Cancel();
            release.Set();
            await first;
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(1, backend.Refreshes);
            Assert.Equal(1, backend.Reads);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CancellationDuringRefreshWaitsForItsReturnAndSkipsReading()
    {
        var trace = new Trace();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource started = Signal();
        var backend = new Backend(trace) { OnRefresh = () => Block(started, release) };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        Task<SensorQueryResult> query = session.QueryAsync(Field(), cancellation.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(query.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query);
            Assert.Equal(0, backend.Reads);
            backend.OnRefresh = null;
            Assert.Equal(12.5d, (await session.QueryAsync(Field())).Value);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ShutdownFinishesTheActiveRequestAndRejectsPendingRequests()
    {
        var trace = new Trace();
        using var release = new ManualResetEventSlim();
        TaskCompletionSource started = Signal();
        var backend = new Backend(trace) { OnRefresh = () => Block(started, release) };
        await using var session = new SensorSession(() => backend, new Apartment(trace));
        Task<SensorQueryResult> first = session.QueryAsync(Field()).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<SensorQueryResult> pending = session.QueryAsync(Field()).AsTask();
            Task stopping = session.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            release.Set();
            Assert.Equal(12.5d, (await first).Value);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, backend.Refreshes);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session.QueryAsync(Field()));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task PumpFailureFaultsPendingRequestsAndStillCleansUp()
    {
        var trace = new Trace();
        var apartment = new Apartment(trace);
        using var release = new ManualResetEventSlim();
        TaskCompletionSource started = Signal();
        var backend = new Backend(trace) { OnRefresh = () => Block(started, release) };
        var session = new SensorSession(() => backend, apartment);
        Task<SensorQueryResult> first = session.QueryAsync(Field()).AsTask();
        var failure = new InvalidOperationException("pump失敗");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<SensorQueryResult> pending = session.QueryAsync(Field()).AsTask();
            apartment.PumpFailure = failure;
            release.Set();
            await first;
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending));
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());
            Assert.Same(failure, Assert.Single(error.InnerExceptions));
            Assert.Contains(trace.Calls, call => call.Operation == "dispose");
            Assert.Contains(trace.Calls, call => call.Operation == "uninitialize");
        }
        finally
        {
            release.Set();
            await ObserveShutdown(session);
        }
    }

    [Fact]
    public async Task InitializationFailureDoesNotConstructOrUninitializeAController()
    {
        var trace = new Trace();
        var apartment = new Apartment(trace) { InitializeResult = unchecked((int)0x80010106) };
        var session = new SensorSession(() => throw new InvalidOperationException("未到達"), apartment);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(async () => await session.QueryAsync(Field()));
        Assert.Equal(apartment.InitializeResult, error.HResult);
        await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());
        Assert.Equal(["initialize"], trace.Calls.Select(call => call.Operation));
    }

    [Fact]
    public async Task ControllerCreationFailureStillUninitializesTheApartment()
    {
        var trace = new Trace();
        var failure = new InvalidOperationException("controller生成失敗");
        var session = new SensorSession(() => throw failure, new Apartment(trace));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.QueryAsync(Field())));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());
        Assert.Same(failure, Assert.Single(error.InnerExceptions));
        Assert.Equal(["initialize", "uninitialize"], trace.Calls.Select(call => call.Operation));
    }

    [Fact]
    public async Task ShutdownCanBeRequestedWhileControllerCreationIsBlocked()
    {
        var trace = new Trace();
        var backend = new Backend(trace);
        using var release = new ManualResetEventSlim();
        TaskCompletionSource started = Signal();
        await using var session = new SensorSession(() =>
        {
            Block(started, release);
            return backend;
        }, new Apartment(trace));
        Task<SensorQueryResult> query = session.QueryAsync(Field()).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stopping = session.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            release.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await query);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, backend.Refreshes);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task IdlePumpDeliversOwnerThreadReentryWithoutQueueingItself()
    {
        var trace = new Trace();
        var apartment = new Apartment(trace);
        var failure = new InvalidOperationException("通知失敗");
        var backend = new Backend(trace) { CallbackFailure = failure };
        await using var session = new SensorSession(() => backend, apartment);
        await session.QueryAsync(Field());
        Task<Exception?>? reply = null;
        TaskCompletionSource delivered = Signal();
        apartment.Messages.Enqueue(() =>
        {
            ValueTask<Exception?> result = session.TakeCallbackFailureAsync();
            Assert.True(result.IsCompletedSuccessfully);
            reply = result.AsTask();
            delivered.TrySetResult();
        });
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(failure, await reply!);
        Assert.Null(await session.TakeCallbackFailureAsync());
    }

    [Fact]
    public async Task AllCleanupFailuresAreReportedAfterBothCleanupSteps()
    {
        var trace = new Trace();
        var disposeFailure = new InvalidOperationException("controller終了失敗");
        var uninitializeFailure = new InvalidOperationException("COM終了失敗");
        var backend = new Backend(trace) { DisposeFailure = disposeFailure };
        var apartment = new Apartment(trace) { UninitializeFailure = uninitializeFailure };
        var session = new SensorSession(() => backend, apartment);
        await session.QueryAsync(Field());
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());
        Assert.Equal([disposeFailure, uninitializeFailure], error.InnerExceptions);
        Assert.Equal("uninitialize", trace.Calls.Last().Operation);
    }

    [Fact]
    public async Task ActualControllerAndAllItsFakeNativeReferencesRemainOnTheWorkerSta()
    {
        var trace = new Trace();
        var api = new SensorControllerTests.Api();
        api.OnOperation = trace.Record;
        await using var session = new SensorSession(() => new SensorController(api, api, api, api), new Apartment(trace));
        Assert.Equal(1.5d, (await session.QueryAsync(Field())).Value);
        await session.DisposeAsync();
        api.AssertBalanced();
        Assert.Single(trace.Calls.Select(call => call.Thread).Distinct());
        Assert.All(trace.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Block(TaskCompletionSource started, ManualResetEventSlim release)
    {
        started.TrySetResult();
        Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
    }

    private static async Task ObserveShutdown(SensorSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (AggregateException)
        {
            // 失敗時の後始末を待ち、テストの先行した失敗を保ちます。
        }
    }

    private static SensorDataField Field()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(20, out SensorDataField field));
        return field;
    }

    private sealed class Trace
    {
        internal ConcurrentQueue<(string Operation, int Thread, ApartmentState Apartment)> Calls { get; } = new();

        internal void Record(string operation) => Calls.Enqueue((operation,
            Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
    }

    private sealed class Apartment(Trace trace) : ISensorApartment
    {
        internal int InitializeResult;
        internal Exception? PumpFailure;
        internal Exception? UninitializeFailure;
        internal ConcurrentQueue<Action> Messages { get; } = new();

        public int Initialize()
        {
            trace.Record("initialize");
            return InitializeResult;
        }

        public void Pump()
        {
            trace.Record("pump");
            if (PumpFailure is not null)
            {
                throw PumpFailure;
            }

            if (Messages.TryDequeue(out Action? action))
            {
                action();
            }
        }

        public void Uninitialize()
        {
            trace.Record("uninitialize");
            if (UninitializeFailure is not null)
            {
                throw UninitializeFailure;
            }
        }
    }

    private sealed class Backend(Trace trace) : ISensorSessionController
    {
        internal int DiscoveryResult;
        internal bool Found = true;
        internal object? Value = 12.5d;
        internal int Refreshes;
        internal int Reads;
        internal Action? OnRefresh;
        internal Exception? CallbackFailure;
        internal Exception? DisposeFailure;

        public int Refresh()
        {
            trace.Record("refresh");
            Refreshes++;
            OnRefresh?.Invoke();
            return DiscoveryResult;
        }

        public bool TryRead(SensorDataField field, out object? value)
        {
            trace.Record("read");
            Reads++;
            value = Value;
            return Found;
        }

        public Exception? TakeCallbackFailure()
        {
            trace.Record("failure");
            return Interlocked.Exchange(ref CallbackFailure, null);
        }

        public void Dispose()
        {
            trace.Record("dispose");
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }
}
