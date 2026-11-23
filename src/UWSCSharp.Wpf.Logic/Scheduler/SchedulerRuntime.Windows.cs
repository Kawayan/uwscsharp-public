namespace UWSCSharp.Wpf;

internal sealed partial class SchedulerRuntime
{
    private readonly SchedulerWindowMatcher windows = new();
    private Guid windowGeneration;

    internal Exception? WindowObservationError
    {
        get; private set;
    }

    internal Guid? BeginWindowObservation()
    {
        try
        {
            RequireOwner();
            if (!minutes.IsActive)
            {
                return null;
            }

            _ = RefreshConfiguration();
            return minutes.IsActive && applied!.Slots.Any(entry =>
                entry is { Enabled: true, Condition: SchedulerWindowCondition })
                    ? configurationEpoch : null;
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            return null;
        }
    }

    internal int ObserveWindows(Guid epoch, SchedulerWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            RequireOwner();
            if (!minutes.IsActive)
            {
                return 0;
            }

            _ = RefreshConfiguration();
            if (!minutes.IsActive || epoch == Guid.Empty || epoch != configurationEpoch)
            {
                return 0;
            }

            WindowObservationError = snapshot.Error;
            return windows.Observe(epoch, snapshot, (entry, trigger) =>
            {
                RequireOwner();
                return queue.TryEnqueue(new SchedulerExecutionRequest(
                    Guid.NewGuid(), entry, trigger.ObservedAt, trigger));
            });
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            return 0;
        }
    }

    private void ConfigureWindows(SchedulerSettings settings)
    {
        configurationEpoch = windows.Configure(windowGeneration, settings);
        WindowObservationError = null;
    }
}
