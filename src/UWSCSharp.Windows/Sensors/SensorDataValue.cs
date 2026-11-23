using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// センサーの取得値を、読取り種別に従って変換します。
/// </summary>
internal static class SensorDataValue
{
    /// <summary>
    /// 取得先のPROPVARIANTを所有し、値のコピー後に解放します。
    /// </summary>
    /// <remarks>取得処理は、内部ポインターの所有権もこの呼出しへ渡します。</remarks>
    internal static object Read(
        SensorDataValueKind kind,
        Action<nint> getValue,
        ISensorPropertyValueNativeApi? nativeApi = null)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        // Windows用バーコードgetterは、reportを取得しません。
        if (kind == SensorDataValueKind.EmptyString)
        {
            return string.Empty;
        }

        nativeApi ??= SensorPropertyValueNativeApi.Instance;
        int size = IntPtr.Size == 8 ? 24 : 16;
        byte[] initialValue = new byte[size];
        nint propertyValue = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(initialValue, 0, propertyValue, size);
            getValue(propertyValue);
            return kind switch
            {
                SensorDataValueKind.Numeric => ReadNumeric(propertyValue),
                SensorDataValueKind.Boolean => nativeApi.ReadBoolean(propertyValue),
                // RFID getterはVARTYPEで変換せず、unionの64ビットを読みます。
                SensorDataValueKind.Integer => Marshal.ReadInt64(propertyValue, 8),
                SensorDataValueKind.String => nativeApi.ReadString(propertyValue),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        finally
        {
            try
            {
                nativeApi.Clear(propertyValue);
            }
            finally
            {
                Marshal.FreeHGlobal(propertyValue);
            }
        }
    }

    private static double ReadNumeric(nint propertyValue)
    {
        byte[] bytes = new byte[16];
        Marshal.Copy(propertyValue, bytes, 0, bytes.Length);
        return SensorNumericValue.Read(bytes);
    }
}
