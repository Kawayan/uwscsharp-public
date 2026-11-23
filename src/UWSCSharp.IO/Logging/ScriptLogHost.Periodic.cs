using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

public sealed partial class ScriptLogHost
{
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 周期保存と最終保存を行い、保存失敗は実行の終了コードと分けて通知します。
    /// 実行自体の例外と、周期処理の停止・通知の失敗は元の規則で扱います。
    /// </summary>
    public Task<int> RunWithPeriodicFlushAsync(Func<IScriptHost, Task<int>> run,
        Action<Exception> reportSaveError, TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reportSaveError);
        TimeSpan interval = flushInterval ?? DefaultFlushInterval;
        if (interval.TotalMilliseconds < 1
            || interval.TotalMilliseconds > uint.MaxValue - 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }
        return RunWithHostAsync(this, run, reportSaveError,
            interval, reportFinalSaveErrors: true);
    }

    /// <summary>
    /// 主スクリプトと同じフォルダーへ周期保存し、保存失敗を通知します。
    /// 終了時は周期処理の停止を待ってから最終保存します。
    /// </summary>
    public static Task<int> RunScriptAsync(IScriptHost output, string scriptPath,
        Func<IScriptHost, Task<int>> run, Action<Exception> reportSaveError,
        TimeProvider? timeProvider = null, TimeSpan? flushInterval = null)
        => RunScriptWithDefaultsAsync(output, scriptPath,
            new ScriptLogDefaults(), run, reportSaveError,
            timeProvider, flushInterval);

    /// <summary>
    /// 読込み済みの件数を表示・保存へ適用し、周期保存を開始します。
    /// スクリプト内のOPTIONはこの実行だけの設定を上書きします。
    /// </summary>
    public static Task<int> RunScriptWithDefaultsAsync(
        IScriptHost output, string scriptPath, ScriptLogDefaults defaults,
        Func<IScriptHost, Task<int>> run, Action<Exception> reportSaveError,
        TimeProvider? timeProvider = null, TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reportSaveError);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        TimeSpan interval = flushInterval ?? DefaultFlushInterval;
        if (interval.TotalMilliseconds < 1
            || interval.TotalMilliseconds > uint.MaxValue - 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!,
            "UWSC.LOG");
        var host = new ScriptLogHost(output, path, replaceInvalidText: true,
            maximumLines: defaults.MaximumLines, timeProvider: timeProvider);
        return ApplyDefaultsAndRunAsync(host, defaults.MaximumLines,
            run, reportSaveError, interval);
    }

    private static async Task<int> ApplyDefaultsAndRunAsync(
        ScriptLogHost host, int maximumLines,
        Func<IScriptHost, Task<int>> run, Action<Exception> reportSaveError,
        TimeSpan interval)
    {
        await host.SetLogHistoryLimitAsync(maximumLines,
            CancellationToken.None).ConfigureAwait(false);
        return await RunWithHostAsync(host, run, reportSaveError,
            interval).ConfigureAwait(false);
    }

    private static async Task<int> RunWithHostAsync(ScriptLogHost host,
        Func<IScriptHost, Task<int>> run, Action<Exception>? reportSaveError = null,
        TimeSpan? interval = null, bool reportFinalSaveErrors = false)
    {
        using var stop = new CancellationTokenSource();
        LogSaveTimer? timer = reportSaveError is null ? null
            : new LogSaveTimer(interval!.Value, host.clock);
        Task periodic = timer is null ? Task.CompletedTask
            : host.SavePeriodicallyAsync(timer, reportSaveError!, stop.Token);
        Exception? primary = null;
        try
        {
            return await run(host).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                cleanupErrors.Add(failure);
            }

            try
            {
                if (timer is not null)
                {
                    await timer.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                cleanupErrors.Add(failure);
            }

            try
            {
                await periodic.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                cleanupErrors.Add(failure);
            }

            try
            {
                await host.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                if (reportFinalSaveErrors)
                {
                    try
                    {
                        reportSaveError!(failure);
                    }
                    catch (Exception notificationFailure)
                    {
                        cleanupErrors.Add(new AggregateException(
                            "最終ログの保存失敗を通知できませんでした。",
                            failure, notificationFailure));
                    }
                }
                else
                {
                    cleanupErrors.Add(failure);
                }
            }

            if (cleanupErrors.Count > 0)
            {
                if (primary is not null)
                {
                    IEnumerable<Exception> previous = primary.Data["UWSCSharp.CleanupErrors"]
                        is AggregateException errors ? errors.InnerExceptions : [];
                    primary.Data["UWSCSharp.CleanupErrors"] = new AggregateException(
                        previous.Concat(cleanupErrors));
                }
                else if (cleanupErrors.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
                }
                else
                {
                    throw new AggregateException("ログの終了処理で複数のエラーが発生しました。",
                        cleanupErrors);
                }
            }
        }
    }

    private async Task SavePeriodicallyAsync(LogSaveTimer timer,
        Action<Exception> reportSaveError, CancellationToken token)
    {
        (Type Type, string Message)? reported = null;
        try
        {
            while (await timer.WaitForTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(token).ConfigureAwait(false);
                    reported = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    (Type Type, string Message) current = (failure.GetType(), failure.Message);
                    if (reported != current)
                    {
                        try
                        {
                            reportSaveError(failure);
                        }
                        catch (Exception notificationFailure)
                        {
                            throw new AggregateException(
                                "ログの保存失敗を通知できませんでした。", failure, notificationFailure);
                        }

                        reported = current;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 所有する周期処理の取消だけを通常の終了として扱います。
        }
    }

    private sealed class LogSaveTimer : IAsyncDisposable
    {
        private readonly Channel<bool> ticks = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                AllowSynchronousContinuations = false
            });
        private readonly ITimer timer;

        internal LogSaveTimer(TimeSpan interval, TimeProvider clock)
        {
            // 生成失敗時にfinalizerを実行するラッパーを使わず、所有権を明示します。
            timer = clock.CreateTimer(_ => ticks.Writer.TryWrite(true), null,
                interval, interval);
        }

        internal async ValueTask<bool> WaitForTickAsync(CancellationToken token)
        {
            if (await ticks.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                return ticks.Reader.TryRead(out _);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            ticks.Writer.TryComplete();
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
