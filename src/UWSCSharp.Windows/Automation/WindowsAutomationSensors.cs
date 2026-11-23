using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private async ValueTask<Value> InvokeSensorAsync(int selector, CancellationToken token)
        {
            CancellationToken lifetime;
            SensorSession sensor;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                lifetime = runToken;
                sensor = resources.Sensor;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            try
            {
                SensorQueryResult result = await sensor.QueryAsync(selector, cancellation.Token)
                    .ConfigureAwait(false);
                Exception? callback = await sensor.TakeCallbackFailureAsync(cancellation.Token)
                    .ConfigureAwait(false);
                if (callback is not null)
                {
                    throw callback;
                }

                return SensorScriptValue.Convert(result);
            }
            catch (Exception failure) when (WindowsOperationErrors.Contains(failure)
                || failure is COMException
                or InvalidOperationException or ArgumentException)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                throw WindowsOperationErrors.Record(failure);
            }
        }
    }

    private sealed partial class RunResources
    {
        private readonly HashSet<CancellationToken> sensorRootTokens = [];
        private readonly List<CancellationTokenRegistration> sensorRegistrations = [];
        private SensorSession? sensor;
        private Task? sensorStopping;

        internal SensorSession Sensor
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(closing, this);
                    foreach (CancellationToken token in sensorRootTokens)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    return sensor ??= sensorSessionFactory?.Invoke() ?? new SensorSession();
                }
            }
        }

        internal void ConfigureSensorCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (token.CanBeCanceled && sensorRootTokens.Add(token))
                {
                    sensorRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestSensorStop(), this));
                }
            }
        }

        private void RequestSensorStop()
        {
            lock (gate)
            {
                sensorStopping ??= sensor?.DisposeAsync().AsTask()
                    ?? Task.CompletedTask;
            }
        }

        private async Task DisposeSensorAsync(List<Exception> errors)
        {
            Task stopping;
            CancellationTokenRegistration[] registrations;
            lock (gate)
            {
                stopping = sensorStopping ??= sensor?.DisposeAsync().AsTask()
                    ?? Task.CompletedTask;
                registrations = [.. sensorRegistrations];
            }

            try
            {
                // 同期COM要求の終了と解放を待ってから、forkの終了を待ちます。
                await stopping.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(stopping.Exception ?? failure);
            }

            foreach (CancellationTokenRegistration registration in registrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
