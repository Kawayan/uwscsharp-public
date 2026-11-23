using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMonitorResourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-monitor-resources-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    private string LeasePath => Path.Combine(directory, "schedule.monitor");

    [Fact]
    public async Task PreparationRetainsLeaseAndStopPreventsStartupPublication()
    {
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new Resources { Prepare = _ => prepared.Task };
        using SchedulerMonitorSession session = Create(_ => resource);
        Task<bool> starting = session.TryStartAsync(Now, TimeZoneInfo.Utc);
        Assert.True(session.OwnsMonitor);
        Assert.False(session.IsActive);
        Assert.Null(session.TryDispatch());
        Assert.Null(SchedulerMonitorLease.TryAcquire(LeasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.TryStartAsync(Now, TimeZoneInfo.Utc));
        Assert.Throws<InvalidOperationException>(session.Dispose);
        session.StopAccepting();
        prepared.SetResult();
        Assert.False(await starting);
        Assert.False(session.OwnsMonitor);
        Assert.True(resource.Disposed);
        Assert.Null(session.TryDispatch());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreparationRequiresExplicitRetryAndKeepsGeneration(bool aggregate)
    {
        var resource = new Resources
        {
            Prepare = _ => Task.FromException(aggregate
                ? new AggregateException(new IOException("prepare failed"))
                : new IOException("prepare failed"))
        };
        Guid generation = Guid.Empty;
        int created = 0;
        using SchedulerMonitorSession session = Create(lease =>
        {
            generation = lease.Generation;
            created++;
            return resource;
        });
        Assert.False(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        Assert.True(session.OwnsMonitor);
        Assert.Equal(aggregate ? typeof(AggregateException) : typeof(IOException),
            session.LastError?.GetType());
        Assert.Null(session.TryDispatch());
        Assert.Throws<InvalidOperationException>(session.Dispose);
        Assert.Throws<InvalidOperationException>(() => session.TryStart(Now, TimeZoneInfo.Utc));
        session.Poll(Now.AddHours(1), TimeZoneInfo.Utc);
        Assert.Equal(1, resource.Preparations);
        resource.Prepare = _ => Task.CompletedTask;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        SchedulerDispatch dispatch = Assert.IsType<SchedulerDispatch>(session.TryDispatch());
        Assert.Equal(generation, dispatch.Generation);
        Assert.Equal(1, created);
        Assert.True(session.Complete(dispatch,
            SchedulerExecutionResult.Completed(0, true, false, true)));
        session.StopAccepting();
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public async Task ActiveNativeResourcePinsLeaseWithoutDispatchedRequest()
    {
        var resource = new Resources { CanRelease = false };
        using SchedulerMonitorSession session = Create(_ => resource);
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
        Assert.True(session.OwnsMonitor);
        Assert.False(resource.Disposed);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        Assert.Null(SchedulerMonitorLease.TryAcquire(LeasePath));
        resource.CanRelease = true;
        session.StopAccepting();
        Assert.True(resource.Disposed);
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public async Task FactoryFailureCanRetryWithoutReleasingTheLease()
    {
        var resource = new Resources();
        bool fail = true;
        Guid firstGeneration = Guid.Empty;
        using SchedulerMonitorSession session = Create(lease =>
        {
            if (firstGeneration == Guid.Empty)
            {
                firstGeneration = lease.Generation;
            }
            Assert.Equal(firstGeneration, lease.Generation);
            return fail ? throw new InvalidOperationException("factory failed") : resource;
        });
        Assert.False(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        Assert.True(session.OwnsMonitor);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        fail = false;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public async Task CancelledPreparationDoesNotPublishOrReleaseUnconfirmedResources()
    {
        var resource = new Resources
        {
            Prepare = token => Task.Delay(Timeout.Infinite, token)
        };
        using SchedulerMonitorSession session = Create(_ => resource);
        using var cancellation = new CancellationTokenSource();
        Task<bool> starting = session.TryStartAsync(Now, TimeZoneInfo.Utc, cancellation.Token);
        cancellation.Cancel();
        Assert.False(await starting);
        Assert.IsAssignableFrom<OperationCanceledException>(session.LastError);
        Assert.True(session.OwnsMonitor);
        Assert.Null(session.TryDispatch());
        resource.Prepare = _ => Task.CompletedTask;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
    }

    [Fact]
    public async Task ReleaseFailureKeepsLeaseUntilExplicitRecovery()
    {
        var resource = new Resources { FailDispose = true };
        using SchedulerMonitorSession session = Create(_ => resource);
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
        Assert.True(session.OwnsMonitor);
        Assert.False(resource.Disposed);
        Assert.IsType<IOException>(session.LastError);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        resource.FailDispose = false;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
        Assert.True(resource.Disposed);
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public async Task AlreadyCancelledStartDoesNotAcquireOrConstructResources()
    {
        int created = 0;
        using SchedulerMonitorSession session = Create(_ =>
        {
            created++;
            return new Resources();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.TryStartAsync(Now, TimeZoneInfo.Utc, cancellation.Token));
        Assert.Equal(0, created);
        Assert.False(session.OwnsMonitor);
        Assert.False(session.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedPreparationErrorIsRethrownWithoutBlockingExplicitRetry(bool factoryFault)
    {
        var failure = new NullReferenceException("unexpected preparation error");
        var resource = new Resources
        {
            Prepare = _ => factoryFault ? Task.CompletedTask : Task.FromException(failure)
        };
        bool fail = true;
        using SchedulerMonitorSession session = Create(_ =>
            factoryFault && fail ? throw failure : resource);
        Assert.Same(failure, await Assert.ThrowsAsync<NullReferenceException>(() =>
            session.TryStartAsync(Now, TimeZoneInfo.Utc)));
        Assert.Same(failure, session.LastError);
        Assert.True(session.OwnsMonitor);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        fail = false;
        resource.Prepare = _ => Task.CompletedTask;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        session.StopAccepting();
        Assert.False(session.OwnsMonitor);
    }

    private SchedulerMonitorSession Create(
        Func<SchedulerMonitorLease, ISchedulerMonitorResources> factory)
    {
        string settings = Path.Combine(directory, "schedule.json");
        SchedulerSettingsStore.Save(settings,
            new SchedulerSettings([SchedulerSettingsTests.Entry()], enabled: true), null);
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        return new SchedulerMonitorSession(new SchedulerSettingsController(settings),
            new SchedulerApplicationSession(), gate, LeasePath, lease =>
            {
                ISchedulerMonitorResources resource = factory(lease);
                ((Resources)resource).BeforeDispose = () =>
                {
                    Assert.True(lease.IsActive);
                    using var contender = SchedulerMonitorLease.TryAcquire(LeasePath);
                    Assert.Null(contender);
                };
                return resource;
            });
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != parent
            || !Path.GetFileName(target).StartsWith("uwsc-monitor-resources-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーが範囲外です。");
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class Resources : ISchedulerMonitorResources
    {
        internal Func<CancellationToken, Task> Prepare { get; set; } = _ => Task.CompletedTask;

        internal Action? BeforeDispose
        {
            get; set;
        }

        internal bool Disposed
        {
            get; private set;
        }

        internal bool FailDispose
        {
            get; set;
        }

        internal int Preparations
        {
            get; private set;
        }

        public bool CanRelease { get; set; } = true;

        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            Preparations++;
            return Prepare(cancellationToken);
        }

        public void Dispose()
        {
            BeforeDispose?.Invoke();
            if (FailDispose)
            {
                throw new IOException("dispose failed");
            }
            Disposed = true;
        }
    }
}
