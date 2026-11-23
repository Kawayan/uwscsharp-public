using System.Windows.Threading;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal sealed partial class SchedulerRuntimeHost
{
    private readonly DispatcherTimer windowTimer;
    private readonly Func<CancellationToken, Task<IReadOnlyList<WindowsObservedWindow>>> windowSource;
    private Task windowObservation = Task.CompletedTask;
    private CancellationTokenSource? windowCancellation;

    internal Exception? WindowObservationError => monitor.WindowObservationError;

    private void QueueWindowObservation()
    {
        if (disposed || stopping || !monitor.IsActive)
        {
            CancelWindowObservation();
            return;
        }
        if (!windowObservation.IsCompleted)
        {
            return;
        }
        Guid? epoch = monitor.BeginWindowObservation();
        LastError = monitor.LastError;
        if (epoch is null)
        {
            CancelWindowObservation();
            return;
        }
        windowTimer.Start();
        var cancellation = new CancellationTokenSource();
        windowCancellation = cancellation;
        windowObservation = ObserveWindowsAsync(epoch.Value, clock.GetUtcNow(), cancellation);
    }

    private async Task ObserveWindowsAsync(Guid epoch, DateTimeOffset observedAt,
        CancellationTokenSource cancellation)
    {
        try
        {
            SchedulerWindowSnapshot snapshot;
            try
            {
                // 列挙と文字列取得、および観測結果の検査をUIスレッドから分離します。
                snapshot = await Task.Run(async () =>
                {
                    IReadOnlyList<WindowsObservedWindow> windows =
                        await windowSource(cancellation.Token).ConfigureAwait(false);
                    return SchedulerWindowSnapshot.Complete(observedAt, windows.Select(window =>
                        new SchedulerObservedWindow(new SchedulerWindowTarget(
                            unchecked((ulong)(nuint)window.Target.Handle),
                            window.Target.ProcessId, window.Target.ThreadId),
                            window.Title, window.ClassName)));
                }, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                snapshot = SchedulerWindowSnapshot.Failed(observedAt, error);
            }
            if (!disposed && !stopping && !cancellation.IsCancellationRequested)
            {
                monitor.ObserveWindows(epoch, snapshot);
                LastError = monitor.LastError;
                if (!monitor.OwnsMonitor)
                {
                    CancelAll();
                }
                DispatchAvailable();
            }
        }
        catch (Exception error)
        {
            LastError = error;
            monitor.StopAccepting();
            CancelWindowObservation();
        }
        finally
        {
            if (ReferenceEquals(windowCancellation, cancellation))
            {
                windowCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelWindowObservation()
    {
        windowTimer.Stop();
        windowCancellation?.Cancel();
    }
}
