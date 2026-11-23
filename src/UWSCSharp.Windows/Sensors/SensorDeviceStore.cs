namespace UWSCSharp.Windows;

/// <summary>
/// 保持中の機器と同期読取りを、呼出し中だけ使って制御します。
/// </summary>
internal delegate bool SensorDeviceReadOperation(
    SensorDeviceRegistration registration, nint sensor,
    Func<object> read, out object? value);

/// <summary>
/// 登録した順にセンサーと通知reportを所有します。
/// </summary>
internal sealed class SensorDeviceStore : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ISensorReportNativeApi nativeApi;
    private readonly ISensorPropertyValueNativeApi propertyApi;
    private readonly Dictionary<Guid, Entry> devices = [];
    private readonly List<Entry> ordered = [];
    private bool disposed;

    internal SensorDeviceStore(
        ISensorReportNativeApi? nativeApi = null,
        ISensorPropertyValueNativeApi? propertyApi = null)
    {
        this.nativeApi = nativeApi ?? SensorReportNativeApi.Instance;
        this.propertyApi = propertyApi ?? SensorPropertyValueNativeApi.Instance;
    }

    /// <summary>
    /// 同じIDの登録を再利用し、新しい機器だけを末尾へ追加します。
    /// </summary>
    internal SensorDeviceRegistration Register(Guid id, Guid category, nint sensor)
    {
        VerifyAccess();
        if (sensor == 0)
        {
            throw new ArgumentException("センサーがNULLです。", nameof(sensor));
        }

        if (devices.TryGetValue(id, out Entry? existing))
        {
            return existing.Registration;
        }

        var reference = OwnedReference.Retain(nativeApi, sensor);
        bool committed = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (devices.TryGetValue(id, out existing))
            {
                return existing.Registration;
            }

            var registration = new SensorDeviceRegistration(id, category);
            var entry = new Entry(registration, reference);
            ordered.Add(entry);
            try
            {
                devices.Add(id, entry);
            }
            catch
            {
                ordered.Remove(entry);
                throw;
            }

            committed = true;
            return registration;
        }
        finally
        {
            if (!committed)
            {
                reference.Dispose();
            }
        }
    }

    /// <summary>
    /// 現在の登録への通知だけを受け取り、reportを借用元から保持します。
    /// </summary>
    internal bool UpdateReport(SensorDeviceRegistration registration, nint report)
    {
        CheckThread();
        if (!TryGetCurrent(registration, out Entry? entry))
        {
            return false;
        }

        OwnedReference? replacement = null;
        if (report != 0)
        {
            replacement = OwnedReference.Retain(nativeApi, report);
        }

        if (!TryGetCurrent(registration, out entry))
        {
            replacement?.Dispose();
            return false;
        }

        OwnedReference? previous = entry.Report;
        entry.Report = replacement;
        // 古い参照のReleaseが再入しても、新しい状態を上書きしません。
        previous?.Dispose();
        return true;
    }

    /// <summary>
    /// 分類で最初に登録した機器を読み、不在なら値を作りません。
    /// </summary>
    internal bool TryRead(SensorDataField field, out object? value,
        SensorDeviceReadOperation? operation = null, bool locationDefaults = false)
    {
        VerifyAccess();
        value = null;
        Entry? entry = ordered.Find(candidate => candidate.Registration.Category == field.CategoryId);
        if (entry is null)
        {
            return false;
        }

        using OwnedReference.Lease sensor = entry.Sensor.Borrow();
        if (operation is not null)
        {
            return operation(entry.Registration, sensor.Pointer, Read, out value);
        }

        value = Read();
        return true;

        object Read()
        {
            // 開始処理からの通知が終わった時点のreportを保持します。
            using OwnedReference.Lease? report = locationDefaults ? null : entry.Report?.Borrow();
            return SensorReportReader.Read(sensor.Pointer, report?.Pointer ?? 0,
                field, nativeApi, propertyApi, locationDefaults);
        }
    }

    /// <summary>
    /// 指定した登録だけを外し、遅れて届いた別世代の通知は無視します。
    /// </summary>
    internal bool Remove(SensorDeviceRegistration registration)
    {
        CheckThread();
        if (!TryGetCurrent(registration, out Entry? entry))
        {
            return false;
        }

        devices.Remove(registration.Id);
        ordered.Remove(entry);
        entry.Dispose();
        return true;
    }

    public void Dispose()
    {
        CheckThread();
        if (disposed)
        {
            return;
        }

        Entry[] removed = [.. ordered];
        disposed = true;
        ordered.Clear();
        devices.Clear();
        List<Exception>? errors = null;
        foreach (Entry entry in removed)
        {
            try
            {
                entry.Dispose();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("センサー参照の解放に失敗しました。", errors);
        }
    }

    private bool TryGetCurrent(SensorDeviceRegistration registration,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!disposed && devices.TryGetValue(registration.Id, out entry)
            && ReferenceEquals(entry.Registration, registration))
        {
            return true;
        }

        entry = null;
        return false;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("センサーの所有COM threadで操作してください。");
        }
    }

    internal void VerifyAccess()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    internal bool Contains(Guid id)
    {
        VerifyAccess();
        return devices.ContainsKey(id);
    }

    internal bool IsCurrent(SensorDeviceRegistration registration)
    {
        CheckThread();
        return TryGetCurrent(registration, out _);
    }

    private sealed class Entry(SensorDeviceRegistration registration, OwnedReference sensor) : IDisposable
    {
        internal SensorDeviceRegistration Registration { get; } = registration;

        internal OwnedReference Sensor { get; } = sensor;

        internal OwnedReference? Report
        {
            get; set;
        }

        public void Dispose()
        {
            Exception? reportFailure = null;
            try
            {
                Report?.Dispose();
            }
            catch (Exception error)
            {
                reportFailure = error;
                throw;
            }
            finally
            {
                try
                {
                    Sensor.Dispose();
                }
                catch (Exception error) when (reportFailure is not null)
                {
                    throw new AggregateException(reportFailure, error);
                }
            }
        }
    }

    private sealed class OwnedReference(ISensorReportNativeApi nativeApi) : IDisposable
    {
        private nint pointer;
        private int borrowers;
        private bool disposed;

        internal static OwnedReference Retain(ISensorReportNativeApi nativeApi, nint pointer)
        {
            // 管理オブジェクトの確保が失敗しても、COM参照を増やさない順にします。
            var reference = new OwnedReference(nativeApi);
            nativeApi.AddRef(pointer);
            reference.pointer = pointer;
            return reference;
        }

        internal Lease Borrow()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var lease = new Lease(this, pointer);
            borrowers++;
            return lease;
        }

        public void Dispose()
        {
            disposed = true;
            ReleaseIfUnused();
        }

        private void ReleaseIfUnused()
        {
            if (disposed && borrowers == 0 && pointer != 0)
            {
                nint released = pointer;
                pointer = 0;
                nativeApi.Release(released);
            }
        }

        internal sealed class Lease(OwnedReference owner, nint pointer) : IDisposable
        {
            private OwnedReference? owner = owner;
            internal nint Pointer { get; } = pointer;

            public void Dispose()
            {
                OwnedReference? current = owner;
                owner = null;
                if (current is not null)
                {
                    current.borrowers--;
                    current.ReleaseIfUnused();
                }
            }
        }
    }
}

/// <summary>
/// 機器IDが再利用されても通知元の登録を区別する識別子です。
/// </summary>
internal sealed class SensorDeviceRegistration(Guid id, Guid category)
{
    internal Guid Id { get; } = id;

    internal Guid Category { get; } = category;
}
