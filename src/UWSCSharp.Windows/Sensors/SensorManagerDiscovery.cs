using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 権限要求の前後でコレクションを取得し、列挙順に機器を登録します。
/// </summary>
internal static class SensorManagerDiscovery
{
    internal static readonly Guid AllCategories = new("c317c286-c468-4288-9975-d4c4587c442c");

    /// <summary>
    /// 分類取得のHRESULTを返し、それ以外の取得失敗は例外として伝えます。
    /// </summary>
    /// <remarks>所有COM apartment内で呼び出します。実APIの権限要求はGUIを表示し得ます。</remarks>
    internal static int Refresh(nint manager, SensorDeviceStore store,
        ISensorManagerNativeApi? nativeApi = null,
        Action<nint>? registerSensor = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.VerifyAccess();
        if (manager == 0)
        {
            throw new ArgumentException("センサーmanagerがNULLです。", nameof(manager));
        }

        nativeApi ??= SensorManagerNativeApi.Instance;
        nativeApi.AddRef(manager);
        try
        {
            store.VerifyAccess();
            int result = RequestPermissions(manager, store, nativeApi);
            if (result < 0)
            {
                return result;
            }

            store.VerifyAccess();
            return RegisterCollection(manager, store, nativeApi, registerSensor);
        }
        finally
        {
            nativeApi.Release(manager);
        }
    }

    private static int RequestPermissions(nint manager,
        SensorDeviceStore store, ISensorManagerNativeApi nativeApi)
    {
        nint collection = 0;
        try
        {
            int result = nativeApi.GetSensorsByCategory(manager, in AllCategories, out collection);
            if (result < 0)
            {
                return result;
            }

            RequirePointer(collection);
            store.VerifyAccess();
            ThrowForResult(nativeApi.RequestPermissions(manager, 0, collection, true));
            return result;
        }
        finally
        {
            if (collection != 0)
            {
                nativeApi.Release(collection);
            }
        }
    }

    private static int RegisterCollection(nint manager,
        SensorDeviceStore store, ISensorManagerNativeApi nativeApi,
        Action<nint>? registerSensor)
    {
        nint collection = 0;
        try
        {
            int result = nativeApi.GetSensorsByCategory(manager, in AllCategories, out collection);
            if (result < 0)
            {
                return result;
            }

            RequirePointer(collection);
            store.VerifyAccess();
            ThrowForResult(nativeApi.GetCount(collection, out uint count));
            store.VerifyAccess();
            for (uint index = 0; index < count; index++)
            {
                store.VerifyAccess();
                RegisterAt(collection, index, store, nativeApi, registerSensor);
            }

            return result;
        }
        finally
        {
            if (collection != 0)
            {
                nativeApi.Release(collection);
            }
        }
    }

    private static void RegisterAt(nint collection, uint index,
        SensorDeviceStore store, ISensorManagerNativeApi nativeApi,
        Action<nint>? registerSensor)
    {
        nint sensor = 0;
        try
        {
            ThrowForResult(nativeApi.GetAt(collection, index, out sensor));
            RequirePointer(sensor);
            store.VerifyAccess();
            if (registerSensor is not null)
            {
                registerSensor(sensor);
                return;
            }

            ThrowForResult(nativeApi.GetId(sensor, out Guid id));
            if (!store.Contains(id))
            {
                ThrowForResult(nativeApi.GetCategory(sensor, out Guid category));
                store.Register(id, category, sensor);
            }
        }
        finally
        {
            if (sensor != 0)
            {
                nativeApi.Release(sensor);
            }
        }
    }

    private static void ThrowForResult(int result) =>
        SensorNativeErrors.ThrowForResult(result);

    private static void RequirePointer(nint pointer)
    {
        if (pointer == 0)
        {
            throw new COMException("取得したセンサー参照がNULLです。",
                unchecked((int)0x80004003));
        }
    }
}
