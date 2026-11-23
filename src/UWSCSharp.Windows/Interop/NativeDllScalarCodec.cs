using System.Buffers.Binary;

namespace UWSCSharp.Windows;

/// <summary>
/// 変換済みのABI数値を、丸めずにリトルエンディアンの領域へ対応付けます。
/// </summary>
internal static class NativeDllScalarCodec
{
    internal static bool IsSupported(Type? type) =>
        type == typeof(sbyte) || type == typeof(byte)
        || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong)
        || type == typeof(nint) || type == typeof(nuint)
        || type == typeof(float) || type == typeof(double);

    internal static int GetSize(Type type, int? pointerSize = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(byte) || type == typeof(sbyte))
        {
            return 1;
        }

        if (type == typeof(short) || type == typeof(ushort))
        {
            return 2;
        }

        if (type == typeof(int) || type == typeof(uint) || type == typeof(float))
        {
            return 4;
        }

        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double))
        {
            return 8;
        }

        if (type == typeof(nint) || type == typeof(nuint))
        {
            return pointerSize switch
            {
                null => nint.Size,
                4 or 8 => pointerSize.Value,
                _ => throw new ArgumentOutOfRangeException(nameof(pointerSize))
            };
        }

        throw new ArgumentException("ABIの数値・ポインター型を指定してください。", nameof(type));
    }

    internal static byte[] Encode(object value, int? pointerSize = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = new byte[GetSize(value.GetType(), pointerSize)];
        Write(bytes, value);
        return bytes;
    }

    internal static byte[] EncodeArray(Type elementType,
        IReadOnlyList<object?> values, int? pointerSize = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        int size = GetSize(elementType, pointerSize);
        object?[] snapshot = [.. values];
        foreach (object? value in snapshot)
        {
            if (value?.GetType() != elementType)
            {
                throw new ArgumentException("全要素を宣言したABI型へ変換してください。", nameof(values));
            }
        }

        byte[] bytes = new byte[checked(size * snapshot.Length)];
        for (int index = 0; index < snapshot.Length; index++)
        {
            Write(bytes.AsSpan(index * size, size), snapshot[index]!);
        }

        return bytes;
    }

    internal static object[] DecodeArray(Type elementType,
        ReadOnlySpan<byte> bytes, int? pointerSize = null)
    {
        int size = GetSize(elementType, pointerSize);
        if (bytes.Length % size != 0)
        {
            throw new ArgumentException("領域の長さが要素サイズの倍数ではありません。", nameof(bytes));
        }

        object[] values = new object[bytes.Length / size];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Decode(elementType, bytes.Slice(index * size, size), pointerSize);
        }

        return values;
    }

    internal static object Decode(Type type, ReadOnlySpan<byte> bytes,
        int? pointerSize = null)
    {
        if (bytes.Length != GetSize(type, pointerSize))
        {
            throw new ArgumentException("領域の長さとABI型のサイズが一致しません。", nameof(bytes));
        }

        // 条件式で数値が共通型へ昇格しないよう、各分岐でボックス化します。
        if (type == typeof(byte))
        {
            return bytes[0];
        }
        if (type == typeof(sbyte))
        {
            return unchecked((sbyte)bytes[0]);
        }
        if (type == typeof(short))
        {
            return BinaryPrimitives.ReadInt16LittleEndian(bytes);
        }
        if (type == typeof(ushort))
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }
        if (type == typeof(int))
        {
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
        if (type == typeof(uint))
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        if (type == typeof(long))
        {
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }
        if (type == typeof(ulong))
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        if (type == typeof(float))
        {
            return BinaryPrimitives.ReadSingleLittleEndian(bytes);
        }
        if (type == typeof(double))
        {
            return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        }
        if (type == typeof(nint))
        {
            return bytes.Length == 8
                ? checked((nint)BinaryPrimitives.ReadInt64LittleEndian(bytes))
                : (nint)BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        return bytes.Length == 8
            ? checked((nuint)BinaryPrimitives.ReadUInt64LittleEndian(bytes))
            : (nuint)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal static void Write(Span<byte> bytes, object value)
    {
        switch (value)
        {
            case byte number:
                bytes[0] = number;
                break;
            case sbyte number:
                bytes[0] = unchecked((byte)number);
                break;
            case short number:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, number);
                break;
            case ushort number:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, number);
                break;
            case int number:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, number);
                break;
            case uint number:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, number);
                break;
            case long number:
                BinaryPrimitives.WriteInt64LittleEndian(bytes, number);
                break;
            case ulong number:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, number);
                break;
            case float number:
                BinaryPrimitives.WriteSingleLittleEndian(bytes, number);
                break;
            case double number:
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, number);
                break;
            case nint number:
                if (bytes.Length == 8)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, number);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)number));
                }

                break;
            case nuint number:
                if (bytes.Length == 8)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes, number);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)number));
                }

                break;
            default:
                throw new ArgumentException("ABIの数値・ポインター値を指定してください。", nameof(value));
        }
    }
}
