using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// managerの分類取得・権限要求と機器の列挙を扱います。
/// </summary>
internal interface ISensorManagerNativeApi
{
    void AddRef(nint instance);

    void Release(nint instance);

    int GetSensorsByCategory(nint manager, in Guid category, out nint collection);

    int RequestPermissions(nint manager, nint parent, nint collection, bool modal);

    int GetCount(nint collection, out uint count);

    int GetAt(nint collection, uint index, out nint sensor);

    int GetId(nint sensor, out Guid id);

    int GetCategory(nint sensor, out Guid category);
}

internal sealed class SensorManagerNativeApi : ISensorManagerNativeApi
{
    internal static SensorManagerNativeApi Instance { get; } = new();

    private SensorManagerNativeApi()
    {
    }

    public void AddRef(nint instance) => Marshal.AddRef(instance);

    public void Release(nint instance) => Marshal.Release(instance);

    public int GetSensorsByCategory(nint manager, in Guid category, out nint collection) =>
        GetSlot<GetCollectionCall>(manager, 3)(manager, in category, out collection);

    public int RequestPermissions(nint manager, nint parent, nint collection, bool modal) =>
        GetSlot<RequestPermissionsCall>(manager, 7)(manager, parent, collection, modal ? 1 : 0);

    public int GetCount(nint collection, out uint count) =>
        GetSlot<GetCountCall>(collection, 4)(collection, out count);

    public int GetAt(nint collection, uint index, out nint sensor) =>
        GetSlot<GetAtCall>(collection, 3)(collection, index, out sensor);

    public int GetId(nint sensor, out Guid id) =>
        GetSlot<GetGuidCall>(sensor, 3)(sensor, out id);

    public int GetCategory(nint sensor, out Guid category) =>
        GetSlot<GetGuidCall>(sensor, 4)(sensor, out category);

    private static T GetSlot<T>(nint instance, int slot) where T : Delegate
    {
        if (instance == 0)
        {
            throw new ArgumentException("COMインターフェイスがNULLです。", nameof(instance));
        }

        nint table = Marshal.ReadIntPtr(instance);
        nint method = Marshal.ReadIntPtr(table, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCollectionCall(nint manager, in Guid category, out nint collection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestPermissionsCall(nint manager, nint parent, nint collection, int modal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountCall(nint collection, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAtCall(nint collection, uint index, out nint sensor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidCall(nint sensor, out Guid value);
}
