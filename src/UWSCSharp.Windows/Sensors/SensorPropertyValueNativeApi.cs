using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// センサー取得値の型変換と、内部資源の解放を行います。
/// </summary>
internal interface ISensorPropertyValueNativeApi
{
    bool ReadBoolean(nint propertyValue);

    string ReadString(nint propertyValue);

    void Clear(nint propertyValue);
}

internal sealed partial class SensorPropertyValueNativeApi : ISensorPropertyValueNativeApi
{
    internal static SensorPropertyValueNativeApi Instance { get; } = new();

    private SensorPropertyValueNativeApi()
    {
    }

    public bool ReadBoolean(nint propertyValue) =>
        PropVariantToBooleanWithDefault(propertyValue, 0) != 0;

    public string ReadString(nint propertyValue)
    {
        nint defaultText = Marshal.StringToCoTaskMemUni("<no value>");
        try
        {
            nint text = PropVariantToStringWithDefault(propertyValue, defaultText);
            // 取得値と既定値の両方を、コピーが終わるまで保持します。
            return Marshal.PtrToStringUni(text) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(defaultText);
        }
    }

    public void Clear(nint propertyValue)
    {
        // 原版と同じく、解放のHRESULTは取得値へ置き換えません。
        _ = PropVariantClear(propertyValue);
    }
}
