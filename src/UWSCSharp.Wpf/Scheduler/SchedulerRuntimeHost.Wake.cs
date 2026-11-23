namespace UWSCSharp.Wpf;

internal sealed partial class SchedulerRuntimeHost
{
    private Task wakePlanning = Task.CompletedTask;
    private CancellationTokenSource? wakeCancellation;
    private SchedulerWakeRequest? lastWakeRequest;

    internal Exception? WakeError => monitor.WakeError;

    internal DateTimeOffset? WakeTime => native?.WakeTime;

    private void QueueWakePlanning()
    {
        SchedulerWakeRequest? request = !disposed && !stopping
            ? monitor.BeginWakePlanning(clock.GetUtcNow(), timeZone()) : null;
        LastError = monitor.LastError;
        if (!monitor.OwnsMonitor)
        {
            CancelAll();
        }
        if (request is null)
        {
            CancelWakePlanning();
            return;
        }
        if (ReferenceEquals(request, lastWakeRequest))
        {
            return;
        }
        wakeCancellation?.Cancel();
        if (!wakePlanning.IsCompleted)
        {
            return;
        }
        lastWakeRequest = request;
        var cancellation = new CancellationTokenSource();
        wakeCancellation = cancellation;
        wakePlanning = PlanWakeAsync(request, cancellation);
    }

    private async Task PlanWakeAsync(SchedulerWakeRequest request, CancellationTokenSource cancellation)
    {
        try
        {
            SchedulerWakePlan plan = await Task.Run(() =>
            {
                SchedulerWakePlan computed = SchedulerWakePlanner.Find(request.Settings,
                    request.Instant, request.Zone, request.Watermark, cancellation.Token);
                diagnostics?.WakePlanComputed?.Invoke(computed);
                return computed;
            }, cancellation.Token);
            if (!disposed && !stopping && !cancellation.IsCancellationRequested)
            {
                monitor.ApplyWakePlan(request, plan, clock.GetUtcNow(), timeZone());
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!disposed && !cancellation.IsCancellationRequested)
            {
                monitor.ReportWakeFailure(request, error);
            }
        }
        finally
        {
            if (ReferenceEquals(wakeCancellation, cancellation))
            {
                wakeCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelWakePlanning()
    {
        wakeCancellation?.Cancel();
        lastWakeRequest = null;
    }
}
