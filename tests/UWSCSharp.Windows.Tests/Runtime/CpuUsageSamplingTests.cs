using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の累積カウンターと単調時計だけを使い、画面・入力・実OSのCPUは取得しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CpuUsageSamplingTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void SamplesDeltaAfterOneSecondAndCachesShortIntervals()
    {
        var clock = new Clock();
        CpuTimes current = new(100, 200, 100);
        int reads = 0;
        var sampler = new CpuUsageSampler(() => { reads++; return current; }, clock);
        Assert.Equal(0, sampler.Query(default));
        current = new(200, 400, 100);
        clock.Ticks = 999;
        Assert.Equal(0, sampler.Query(default));
        Assert.Equal(1, reads);
        clock.Ticks = 1000;
        Assert.Equal(50, sampler.Query(default));
        Assert.Equal(2, reads);
        clock.Ticks = 1999;
        Assert.Equal(50, sampler.Query(default));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(100, 100, 0, 0)]
    [InlineData(0, 100, 0, 100)]
    [InlineData(30, 100, 0, 70)]
    [InlineData(30, 100, 100, 85)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(201, 100, 100, 0)]
    public void UsesKernelIncludingIdleAndRejectsImpossibleDeltas(
        ulong idle, ulong kernel, ulong user, double expected)
    {
        var clock = new Clock();
        CpuTimes current = new(0, 0, 0);
        var sampler = new CpuUsageSampler(() => current, clock);
        sampler.Query(default);
        clock.Ticks = 1000;
        current = new(idle, kernel, user);
        Assert.Equal(expected, sampler.Query(default));
    }

    [Fact]
    public void CounterAndClockResetStartANewBaseline()
    {
        var clock = new Clock();
        CpuTimes current = new(100, 100, 100);
        var sampler = new CpuUsageSampler(() => current, clock);
        sampler.Query(default);
        clock.Ticks = 1000;
        current = new(1, 2, 3);
        Assert.Equal(0, sampler.Query(default));
        clock.Ticks = 2000;
        current = new(2, 3, 4);
        Assert.Equal(50, sampler.Query(default));
        clock.Ticks = 500;
        Assert.Equal(0, sampler.Query(default));
    }

    [Fact]
    public void DifferentProcessorGroupsDoNotShareADelta()
    {
        var clock = new Clock();
        CpuTimes current = new(0, 100, 0, 0);
        var sampler = new CpuUsageSampler(() => current, clock);
        Assert.Equal(0, sampler.Query(default));
        clock.Ticks = 1000;
        current = new(10, 200, 0, 1);
        Assert.Equal(0, sampler.Query(default));
        clock.Ticks = 2000;
        current = new(60, 300, 0, 1);
        Assert.Equal(50, sampler.Query(default));
    }

    [Fact]
    public async Task UnknownProviderFailuresKeepTheHostFailureClassification()
    {
        var failure = new InvalidOperationException("counter host");
        await using IWindowsAutomationSession session = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, cpuTimes: () => throw failure).CreateSession();
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("CPUUSERATE", [], default).AsTask());
        Assert.Same(failure, actual);
        Assert.Null(session.GetScriptErrorMessage("CPUUSERATE", failure));
    }

    [Fact]
    public async Task SessionsAndForksHaveIndependentWarmupState()
    {
        var clock = new Clock();
        CpuTimes current = new(0, 0, 0);
        var automation = new WindowsAutomation(_ => { }, swappedMouseButtons: false,
            cpuTimes: () => current, cpuClock: clock);
        await using IWindowsAutomationSession session = automation.CreateSession();
        Assert.Equal(0, (await session.InvokeAsync("CPUUSERATE", [], default)).Number);
        clock.Ticks = 1000;
        current = new(25, 100, 0);
        Assert.Equal(75, (await session.InvokeAsync("CPUUSERATE", [], default)).Number);
        await using IWindowsAutomationSession fork = session.Fork();
        Assert.Equal(0, (await fork.InvokeAsync("CPUUSERATE", [], default)).Number);
        Assert.Equal(75, (await session.InvokeAsync("CPUUSERATE", [], default)).Number);
    }

    [Fact]
    public void CancellationBeforeAndAfterProviderDoesNotCommitBaseline()
    {
        var clock = new Clock();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        int reads = 0;
        using var inside = new CancellationTokenSource();
        var sampler = new CpuUsageSampler(() =>
        {
            reads++;
            inside.Cancel();
            return new(0, 0, 0);
        }, clock);
        Assert.Throws<OperationCanceledException>(() => sampler.Query(canceled.Token));
        Assert.Equal(0, reads);
        Assert.Throws<OperationCanceledException>(() => sampler.Query(inside.Token));
        Assert.Equal(1, reads);
        Assert.Equal(0, sampler.Query(default));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ConcurrentCallsCommitOneSamplePerInterval()
    {
        var clock = new Clock();
        int reads = 0;
        var sampler = new CpuUsageSampler(() =>
        {
            Interlocked.Increment(ref reads);
            return new(0, 0, 0);
        }, clock);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => sampler.Query(default))));
        Assert.Equal(1, reads);
    }
}
