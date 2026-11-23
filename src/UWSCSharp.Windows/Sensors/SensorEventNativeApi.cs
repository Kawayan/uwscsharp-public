using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal interface ISensorEventNativeApi
{
    void AddRef(nint source);

    void Release(nint instance);

    nint GetSinkPointer(SensorCallbackSink sink);

    int SetSink(nint source, nint sink, bool manager);
}

internal sealed class SensorEventNativeApi : ISensorEventNativeApi
{
    internal static SensorEventNativeApi Instance { get; } = new();

    private SensorEventNativeApi()
    {
    }

    public void AddRef(nint source) => Marshal.AddRef(source);

    public void Release(nint instance) => Marshal.Release(instance);

    public nint GetSinkPointer(SensorCallbackSink sink) =>
        Marshal.GetComInterfaceForObject(sink, sink.Contract);

    public int SetSink(nint source, nint sink, bool manager)
    {
        if (source == 0)
        {
            throw new ArgumentException("通知元のCOMインターフェイスがNULLです。", nameof(source));
        }

        nint table = Marshal.ReadIntPtr(source);
        int slot = manager ? 6 : 17;
        nint method = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<SetSinkCall>(method)(source, sink);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSinkCall(nint source, nint sink);
}
