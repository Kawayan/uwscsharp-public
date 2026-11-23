namespace UWSCSharp.Wpf;

/// <summary>
/// 遅延した周期を重ねず、所有者の処理を直列に呼びます。
/// </summary>
internal sealed class SchedulerSerialTimer
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;
    private int running;

    internal SchedulerSerialTimer(TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.interval = interval ?? DefaultInterval;
        if (this.interval.TotalMilliseconds < 1
            || this.interval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    internal async Task RunAsync(Func<CancellationToken, Task> tick,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            throw new InvalidOperationException("予約のタイマーは既に動作中です。");
        }

        try
        {
            using var timer = new PeriodicTimer(interval, timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await tick(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }
}
