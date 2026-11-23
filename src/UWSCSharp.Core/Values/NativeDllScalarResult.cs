using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// ABIから受け取った値を、原版の戻りVariant型を保って変換します。
/// </summary>
internal static class NativeDllScalarResult
{
    internal static Value Convert(NativeDllPrimitive primitive,
        object? result, bool preserveIntegerWidth)
    {
        return primitive switch
        {
            NativeDllPrimitive.Int or NativeDllPrimitive.Long or NativeDllPrimitive.Bool =>
                Integer(Require<int>(result), 3),
            NativeDllPrimitive.Byte or NativeDllPrimitive.Boolean =>
                Byte(Require<byte>(result), preserveIntegerWidth),
            NativeDllPrimitive.Word => preserveIntegerWidth
                ? Integer(Require<ushort>(result), 18) : Integer((int)Require<ushort>(result), 3),
            NativeDllPrimitive.Char => EncodedText.FromWindowsAnsi(
                [Require<byte>(result)]).ToValue(),
            NativeDllPrimitive.Wchar => Value.String(
                new string((char)Require<ushort>(result), 1)),
            NativeDllPrimitive.String or NativeDllPrimitive.Pchar
                or NativeDllPrimitive.Pbyte or NativeDllPrimitive.Wstring
                or NativeDllPrimitive.Pwchar => Require<Value>(result),
            NativeDllPrimitive.Uint or NativeDllPrimitive.Dword => Value.Numeric(Require<uint>(result)),
            NativeDllPrimitive.Hwnd or NativeDllPrimitive.Hmenu =>
                Integer((ulong)Require<nuint>(result), 5),
            NativeDllPrimitive.Float => Value.Numeric(Require<float>(result)),
            NativeDllPrimitive.Double or NativeDllPrimitive.Extended =>
                Value.Numeric(Require<double>(result)),
            NativeDllPrimitive.Longlong => Integer(Require<long>(result), 20),
            NativeDllPrimitive.Void when result is null => Byte(0, preserveIntegerWidth),
            NativeDllPrimitive.Void => throw new ArgumentException("VOIDの戻り値はnullにしてください。", nameof(result)),
            _ => throw new NotSupportedException("このDLL戻り型の値変換は未実装です。")
        };
    }

    // 配列引数のUINT値は、通常のUINT戻り値と異なる型で書き戻します。
    internal static Value UnsignedArrayValue(uint value) => Integer(value, 19);

    internal static Value UnsignedArrayValue(nuint value) => value <= uint.MaxValue
        ? UnsignedArrayValue((uint)value)
        : Integer((ulong)value, 21);

    private static Value Byte(byte value, bool preserveIntegerWidth) =>
        preserveIntegerWidth ? Integer(value, 17) : Integer((int)value, 3);

    private static Value Integer<T>(T value, ushort variantType) where T : struct, IConvertible =>
        Value.Numeric(value.ToDouble(CultureInfo.InvariantCulture)) with
        {
            NativeVariantType = variantType,
            NativeScalar = value
        };

    private static T Require<T>(object? value) where T : struct => value is T typed
        ? typed : throw new ArgumentException("戻り値のCLR型が宣言したABI型と一致しません。", nameof(value));
}
