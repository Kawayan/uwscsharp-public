using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// センサーのPROPVARIANTから、対応する数値scalarを読み取ります。
/// </summary>
internal static class SensorNumericValue
{
    internal static double MissingValue =>
        BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000000));

    /// <summary>
    /// 数値以外は原版の数値取得経路と同じ負のquiet NaNを返します。
    /// </summary>
    /// <remarks>借用した領域を読み取り、ポインターの参照や解放は行いません。</remarks>
    internal static double Read(ReadOnlySpan<byte> propertyValue)
    {
        if (propertyValue.Length < 16)
        {
            throw new ArgumentException(
                "PROPVARIANTのscalar領域には16バイト以上が必要です。",
                nameof(propertyValue));
        }

        var type = (VarEnum)BinaryPrimitives.ReadUInt16LittleEndian(propertyValue);
        ReadOnlySpan<byte> scalar = propertyValue[8..];
        return type switch
        {
            VarEnum.VT_I1 => unchecked((sbyte)scalar[0]),
            VarEnum.VT_UI1 => scalar[0],
            VarEnum.VT_I2 => BinaryPrimitives.ReadInt16LittleEndian(scalar),
            VarEnum.VT_UI2 => BinaryPrimitives.ReadUInt16LittleEndian(scalar),
            VarEnum.VT_I4 => BinaryPrimitives.ReadInt32LittleEndian(scalar),
            VarEnum.VT_UI4 => BinaryPrimitives.ReadUInt32LittleEndian(scalar),
            VarEnum.VT_I8 => BinaryPrimitives.ReadInt64LittleEndian(scalar),
            VarEnum.VT_UI8 => BinaryPrimitives.ReadUInt64LittleEndian(scalar),
            VarEnum.VT_R4 => BinaryPrimitives.ReadSingleLittleEndian(scalar),
            VarEnum.VT_R8 => BinaryPrimitives.ReadDoubleLittleEndian(scalar),
            _ => MissingValue
        };
    }
}
