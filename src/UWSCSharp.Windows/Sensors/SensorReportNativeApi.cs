using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// センサーとreportのCOM参照・データ取得を扱います。
/// </summary>
internal interface ISensorReportNativeApi
{
    void AddRef(nint instance);

    void Release(nint instance);

    int GetData(nint sensor, out nint report);

    int GetSensorValue(nint report, in SensorPropertyKey key, nint propertyValue);
}

internal sealed class SensorReportNativeApi : ISensorReportNativeApi
{
    internal static SensorReportNativeApi Instance { get; } = new();

    private SensorReportNativeApi()
    {
    }

    public void AddRef(nint instance) => Marshal.AddRef(instance);

    public void Release(nint instance) => Marshal.Release(instance);

    public int GetData(nint sensor, out nint report) =>
        GetSlot<GetDataCall>(sensor, 13)(sensor, out report);

    public int GetSensorValue(nint report, in SensorPropertyKey key, nint propertyValue) =>
        GetSlot<GetValueCall>(report, 4)(report, in key, propertyValue);

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
    private delegate int GetDataCall(nint sensor, out nint report);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetValueCall(nint report, in SensorPropertyKey key, nint propertyValue);
}
