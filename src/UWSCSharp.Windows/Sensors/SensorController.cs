using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有COM apartment内でmanager・機器・通知接続を管理します。
/// </summary>
internal sealed class SensorController : ISensorSessionController
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ISensorManagerFactory factory;
    private readonly ISensorManagerNativeApi managerApi;
    private readonly ISensorEventNativeApi eventApi;
    private readonly SensorDeviceStore store;
    private readonly Action? updated;
    private readonly SensorCallbackFailure callbackFailure = new();
    private readonly Dictionary<Guid, Binding> bindings = [];
    private nint manager;
    private SensorManagerEventSink? managerSink;
    private SensorEventConnection? managerConnection;
    private bool disposed;
    private bool refreshing;

    internal SensorController(ISensorManagerFactory? factory = null,
        ISensorManagerNativeApi? managerApi = null,
        ISensorEventNativeApi? eventApi = null,
        ISensorReportNativeApi? reportApi = null,
        ISensorPropertyValueNativeApi? propertyApi = null,
        Action? updated = null)
    {
        this.factory = factory ?? SensorManagerFactory.Instance;
        this.managerApi = managerApi ?? SensorManagerNativeApi.Instance;
        this.eventApi = eventApi ?? SensorEventNativeApi.Instance;
        store = new SensorDeviceStore(reportApi, propertyApi);
        this.updated = updated;
    }

    /// <summary>
    /// managerを作り直し、通知登録の後に権限要求と列挙を行います。
    /// </summary>
    internal int Refresh()
    {
        VerifyAccess();
        if (refreshing)
        {
            throw new InvalidOperationException("センサー列挙中に列挙を再開できません。");
        }

        refreshing = true;
        try
        {
            DetachManager();
            VerifyAccess();
            nint created = 0;
            Exception? primary = null;
            try
            {
                int result = factory.Create(out created);
                if (result < 0)
                {
                    return result;
                }

                RequirePointer(created);
                VerifyAccess();
                manager = created;
                created = 0;
                managerSink = new SensorManagerEventSink(
                    (sensor, _) => AddSensor(sensor), callbackFailure);
                try
                {
                    managerConnection = SensorEventConnection.Connect(
                        manager, managerSink, eventApi);
                }
                catch (Exception error)
                {
                    Cleanup(error, DetachManager);
                    throw;
                }

                VerifyAccess();
                return SensorManagerDiscovery.Refresh(manager, store,
                    managerApi, AddSensor);
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                if (created != 0)
                {
                    Release(created, primary);
                }
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    internal bool TryRead(SensorDataField field, out object? value)
    {
        VerifyAccess();
        bool location = field.CategoryId == SensorDataFieldCatalog.LocationCategory;
        return store.TryRead(field, out value, location ? ReadLocation : null,
            locationDefaults: location);
    }

    internal Exception? TakeCallbackFailure() => callbackFailure.Take();

    int ISensorSessionController.Refresh() => Refresh();

    bool ISensorSessionController.TryRead(SensorDataField field, out object? value) =>
        TryRead(field, out value);

    Exception? ISensorSessionController.TakeCallbackFailure() => TakeCallbackFailure();

    public void Dispose()
    {
        CheckThread();
        if (disposed)
        {
            return;
        }

        Binding[] removed = [.. bindings.Values];
        disposed = true;
        bindings.Clear();
        managerSink?.Close();
        foreach (Binding binding in removed)
        {
            binding.Sink?.Close();
        }

        List<Exception>? errors = null;
        foreach (Binding binding in removed)
        {
            try
            {
                CloseBinding(binding);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        try
        {
            DetachManager();
        }
        catch (Exception error)
        {
            (errors ??= []).Add(error);
        }

        try
        {
            store.Dispose();
        }
        catch (Exception error)
        {
            (errors ??= []).Add(error);
        }

        if (errors is not null)
        {
            throw new AggregateException("センサーの終了処理に失敗しました。", errors);
        }
    }

    private void AddSensor(nint sensor)
    {
        VerifyAccess();
        RequirePointer(sensor);
        ThrowForResult(managerApi.GetId(sensor, out Guid id));
        VerifyAccess();
        if (bindings.ContainsKey(id))
        {
            return;
        }

        ThrowForResult(managerApi.GetCategory(sensor, out Guid category));
        VerifyAccess();
        if (bindings.ContainsKey(id)
            || !SensorDataFieldCatalog.SupportsCategory(category))
        {
            return;
        }

        // 接続が通知へ再入しても、同じIDの登録を重ねません。
        var binding = new Binding(id);
        bindings.Add(id, binding);
        try
        {
            binding.Registration = store.Register(id, category, sensor);
            VerifyAccess();
            if (category != SensorDataFieldCatalog.LocationCategory)
            {
                ConnectBinding(binding, sensor);
            }
        }
        catch (ObjectDisposedException) when (binding.Closed && !disposed)
        {
            // 登録中に離脱した機器は、閉じたsinkのまま登録を取り消します。
        }
        catch (Exception primary)
        {
            Cleanup(primary, () => RemoveBinding(binding));
            throw;
        }
    }

    private void ConnectBinding(Binding binding, nint sensor)
    {
        var receiver = new SensorDeviceEventReceiver(store,
            binding.Registration!, updated, left: () => RemoveBinding(binding));
        binding.Sink = new SensorEventSink(receiver, callbackFailure);
        binding.Connection = SensorEventConnection.Connect(sensor, binding.Sink, eventApi);
    }

    private bool ReadLocation(SensorDeviceRegistration registration,
        nint sensor, Func<object> read, out object? value)
    {
        value = null;
        if (!bindings.TryGetValue(registration.Id, out Binding? binding)
            || binding.Closed || !ReferenceEquals(binding.Registration, registration))
        {
            return false;
        }

        // 接続中の同期通知へ再入しても、同じ取得の接続を作り直しません。
        bool first = binding.LocationReads++ == 0;
        Exception? primary = null;
        try
        {
            if (first)
            {
                ConnectBinding(binding, sensor);
            }

            VerifyAccess();
            if (binding.Closed || !store.IsCurrent(registration))
            {
                return false;
            }

            value = read();
            return true;
        }
        catch (ObjectDisposedException) when (binding.Closed && !disposed)
        {
            // 開始通知中に離脱した登録は、保持参照を読む前に不在へ戻します。
            return false;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (--binding.LocationReads == 0)
            {
                if (primary is null)
                {
                    StopLocation(binding);
                }
                else
                {
                    Cleanup(primary, () => StopLocation(binding));
                }
            }
        }
    }

    private static void StopLocation(Binding binding)
    {
        SensorEventConnection? connection = binding.Connection;
        SensorEventSink? sink = binding.Sink;
        binding.Connection = null;
        binding.Sink = null;
        sink?.Close();
        connection?.Dispose();
    }

    private void RemoveBinding(Binding binding)
    {
        if (bindings.TryGetValue(binding.Id, out Binding? current)
            && ReferenceEquals(current, binding))
        {
            bindings.Remove(binding.Id);
            CloseBinding(binding);
        }
    }

    private void CloseBinding(Binding binding)
    {
        binding.Closed = true;
        binding.Sink?.Close();
        SensorEventConnection? connection = binding.Connection;
        binding.Connection = null;
        try
        {
            connection?.Dispose();
        }
        finally
        {
            if (binding.Registration is not null)
            {
                store.Remove(binding.Registration);
            }
        }
    }

    private void DetachManager()
    {
        nint released = manager;
        SensorEventConnection? connection = managerConnection;
        manager = 0;
        managerConnection = null;
        managerSink?.Close();
        managerSink = null;
        Exception? primary = null;
        try
        {
            connection?.Dispose();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (released != 0)
            {
                Release(released, primary);
            }
        }
    }

    private void Release(nint instance, Exception? primary)
    {
        try
        {
            managerApi.Release(instance);
        }
        catch (Exception cleanup) when (primary is not null)
        {
            AddCleanup(primary, cleanup);
        }
    }

    private static void Cleanup(Exception primary, Action action)
    {
        try
        {
            action();
        }
        catch (Exception cleanup)
        {
            AddCleanup(primary, cleanup);
        }
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "UWSCSharp.SensorControllerCleanup";
        primary.Data[key] = primary.Data[key] is Exception previous
            ? new AggregateException(previous, cleanup) : cleanup;
    }

    private void VerifyAccess()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("センサーの所有COM threadで操作してください。");
        }
    }

    private static void ThrowForResult(int result) =>
        SensorNativeErrors.ThrowForResult(result);

    private static void RequirePointer(nint pointer)
    {
        if (pointer == 0)
        {
            throw new COMException("センサー参照がNULLです。", unchecked((int)0x80004003));
        }
    }

    private sealed class Binding(Guid id)
    {
        internal Guid Id { get; } = id;

        internal SensorDeviceRegistration? Registration;
        internal SensorEventSink? Sink;
        internal SensorEventConnection? Connection;
        internal bool Closed;
        internal int LocationReads;
    }
}
