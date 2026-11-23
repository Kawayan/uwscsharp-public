namespace UWSCSharp.Windows;

/// <summary>
/// 登録世代を確認し、reportを通知処理の間だけ保持します。
/// </summary>
internal sealed class SensorDeviceEventReceiver(
    SensorDeviceStore store,
    SensorDeviceRegistration registration,
    Action? updated = null,
    Action<int>? stateChanged = null,
    Action? left = null) : ISensorEventReceiver
{
    public void StateChanged(nint sensor, int state)
    {
        if (store.IsCurrent(registration))
        {
            stateChanged?.Invoke(state);
        }
    }

    public void DataUpdated(nint sensor, nint report)
    {
        if (!store.UpdateReport(registration, report))
        {
            return;
        }

        Exception? primary = null;
        try
        {
            updated?.Invoke();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                store.UpdateReport(registration, 0);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                // 解放失敗は追加情報に残し、通知処理の例外を上書きしません。
                const string key = "UWSCSharp.SensorNotificationCleanup";
                primary.Data[key] = primary.Data[key] is Exception previous
                    ? new AggregateException(previous, cleanup) : cleanup;
            }
        }
    }

    public void Event(nint sensor, in Guid eventId, nint data)
    {
        // 原版のカスタムイベント用callbackも値を取得しません。
    }

    public void Leave(in Guid id)
    {
        if (id == registration.Id && store.Remove(registration))
        {
            left?.Invoke();
        }
    }
}
