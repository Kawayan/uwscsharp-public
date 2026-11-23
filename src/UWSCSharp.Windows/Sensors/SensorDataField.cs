using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// WindowsのPROPERTYKEYに対応する、書式GUIDと項目番号です。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct SensorPropertyKey
{
    internal readonly Guid FormatId;
    internal readonly uint PropertyId;

    internal SensorPropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal enum SensorDataValueKind
{
    Numeric,
    Boolean,
    Integer,
    String,
    EmptyString
}

/// <summary>
/// センサーreportから取得する分類・キーと値の読取り方法です。
/// </summary>
internal readonly record struct SensorDataField(
    Guid CategoryId,
    SensorPropertyKey? Key,
    SensorDataValueKind Kind);
