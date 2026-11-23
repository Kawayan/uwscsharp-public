namespace UWSCSharp.Wpf;

internal sealed partial class SchedulerMonitorSession
{
    internal Exception? WindowObservationError
    {
        get
        {
            lock (sync)
            {
                return runtime?.WindowObservationError;
            }
        }
    }

    internal Guid? BeginWindowObservation()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing || preparationFailed || runtime?.IsActive != true)
            {
                return null;
            }

            try
            {
                controller.Reload();
                Guid? epoch = runtime.BeginWindowObservation();
                LastError = runtime.LastError;
                ReleaseIfIdle();
                return epoch;
            }
            catch (Exception error) when (Operational(error))
            {
                Fail(error);
                return null;
            }
        }
    }

    internal int ObserveWindows(Guid epoch, SchedulerWindowSnapshot snapshot)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing || preparationFailed || runtime?.IsActive != true)
            {
                return 0;
            }

            try
            {
                controller.Reload();
                int accepted = runtime.ObserveWindows(epoch, snapshot);
                LastError = runtime.LastError;
                ReleaseIfIdle();
                return accepted;
            }
            catch (Exception error) when (Operational(error))
            {
                Fail(error);
                return 0;
            }
        }
    }
}
