using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// DLL引数の型宣言とABIの対応を保持します。
/// </summary>
internal static class NativeDllScalarInput
{
    // 原版はスカラーの戻り型に付いた配列記号を無視します。
    internal static bool Supports(NativeDllDeclaration declaration) =>
        (declaration.ReturnType is null
            || (declaration.ReturnType is NativeDllScalarType
            { IsByReference: false } result
                && (AbiType(result.Primitive) is not null
                    || IsText(result.Primitive)
                    || result.Primitive == NativeDllPrimitive.Extended)))
        && declaration.Parameters.All(SupportsParameter);

    private static bool SupportsParameter(NativeDllType parameter)
    {
        return parameter switch
        {
            NativeDllScalarType scalar =>
                (AbiType(scalar.Primitive) is Type type && type != typeof(void))
                || (!scalar.IsArray && IsText(scalar.Primitive))
                || scalar.Primitive == NativeDllPrimitive.Extended,
            NativeDllStructureType structure => !structure.IsByReference
                && !structure.IsArray && structure.Fields.Count > 0
                && structure.Fields.All(SupportsParameter),
            _ => false
        };
    }

    internal static bool IsExtendedParameter(NativeDllType type) =>
        type switch
        {
            NativeDllScalarType { Primitive: NativeDllPrimitive.Extended } => true,
            NativeDllStructureType structure => structure.Fields.Any(IsExtendedParameter),
            _ => false
        };

    internal static bool IsHandleByValue(NativeDllScalarType type) =>
        !type.IsByReference && !type.IsArray && type.Primitive is
            NativeDllPrimitive.Hwnd or NativeDllPrimitive.Hmenu;

    internal static bool IsText(NativeDllPrimitive primitive) => primitive is
        NativeDllPrimitive.String or NativeDllPrimitive.Pchar or NativeDllPrimitive.Pbyte
        or NativeDllPrimitive.Wstring or NativeDllPrimitive.Pwchar;

    internal static bool MatchesArguments(NativeDllDeclaration declaration,
        IReadOnlyList<Expr> arguments)
    {
        int index = 0;
        foreach ((NativeDllScalarType type, bool field) in
            ScriptParameters(declaration.Parameters, field: false))
        {
            if (index == arguments.Count
                || arguments[index] is NamedExpr or OmittedExpr
                || (arguments[index] is ReferenceExpr && !field
                    && !type.IsByReference && !type.IsArray))
            {
                return false;
            }

            index++;
        }

        return index == arguments.Count;
    }

    // 構造体のフィールドをスクリプトの引数順に展開します。
    private static IEnumerable<(NativeDllScalarType Type, bool Field)>
        ScriptParameters(IReadOnlyList<NativeDllType> parameters, bool field)
    {
        foreach (NativeDllType parameter in parameters)
        {
            if (parameter is NativeDllStructureType structure)
            {
                foreach ((NativeDllScalarType Type, bool Field) child in
                    ScriptParameters(structure.Fields, field: true))
                {
                    yield return child;
                }
            }
            else
            {
                yield return ((NativeDllScalarType)parameter, field);
            }
        }
    }

    internal static Type? AbiType(NativeDllPrimitive primitive) =>
        primitive switch
        {
            NativeDllPrimitive.Int or NativeDllPrimitive.Long
                or NativeDllPrimitive.Bool => typeof(int),
            NativeDllPrimitive.Uint or NativeDllPrimitive.Dword => typeof(uint),
            NativeDllPrimitive.Hwnd or NativeDllPrimitive.Hmenu => typeof(nuint),
            NativeDllPrimitive.Byte or NativeDllPrimitive.Boolean
                or NativeDllPrimitive.Char => typeof(byte),
            NativeDllPrimitive.Word or NativeDllPrimitive.Wchar => typeof(ushort),
            NativeDllPrimitive.Longlong => typeof(long),
            NativeDllPrimitive.Float => typeof(float),
            NativeDllPrimitive.Double => typeof(double),
            NativeDllPrimitive.Void => typeof(void),
            _ => null
        };

    // 各分岐でボックス化し、数値の共通型への昇格を防ぎます。
    internal static object Convert(NativeDllPrimitive type, Value value,
        ushort controlWord) => type switch
        {
            NativeDllPrimitive.Int or NativeDllPrimitive.Long
                or NativeDllPrimitive.Bool => (object)Int32(value, controlWord),
            NativeDllPrimitive.Uint or NativeDllPrimitive.Dword =>
                (object)unchecked((uint)Int64(value, controlWord)),
            NativeDllPrimitive.Hwnd or NativeDllPrimitive.Hmenu =>
                (object)Handle(value, controlWord),
            NativeDllPrimitive.Byte or NativeDllPrimitive.Boolean =>
                (object)unchecked((byte)Int32(value, controlWord)),
            NativeDllPrimitive.Word =>
                (object)unchecked((ushort)Int32(value, controlWord)),
            NativeDllPrimitive.Char => (object)AnsiCharacter(value),
            NativeDllPrimitive.Wchar => (object)WideCharacter(value),
            NativeDllPrimitive.Longlong => (object)Int64(value, controlWord),
            NativeDllPrimitive.Float => (object)Single(value),
            NativeDllPrimitive.Double =>
                (object)NumericOperations.RuntimeNumber(value),
            NativeDllPrimitive.Extended =>
                Extended(value, controlWord),
            _ => throw new InvalidOperationException(
                "DLL引数の型には対応していません。")
        };

    private static byte AnsiCharacter(Value value)
    {
        if (value.Kind == ValueKind.Null)
        {
            return 0;
        }

        byte[] bytes = EncodedText.GetWindowsAnsiBytes(value);
        return bytes.Length == 0 ? (byte)0 : bytes[0];
    }

    private static ushort WideCharacter(Value value)
    {
        string text = value.Kind == ValueKind.Null ? "" : BuiltinText.Convert(value);
        return text.Length == 0 ? (ushort)0 : text[0];
    }

    private static int Int32(Value value, ushort controlWord) =>
        checked((int)Int64(value, controlWord));

    private static long Int64(Value value, ushort controlWord)
    {
        if (value.GetUnchangedNativeScalar() is long exact)
        {
            return exact;
        }

        if (value.Kind == ValueKind.String && long.TryParse(value.Text,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        return checked((long)Round(value, controlWord));
    }

    private static nuint Handle(Value value, ushort controlWord)
    {
        if (value.Kind == ValueKind.Null)
        {
            return 0;
        }

        object? exact = value.GetUnchangedNativeScalar();
        if (exact is ulong unsigned)
        {
            return checked((nuint)unsigned);
        }
        if (exact is long signed)
        {
            return unchecked((nuint)signed);
        }

        if (value.Kind == ValueKind.String && ulong.TryParse(value.Text,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return checked((nuint)parsed);
        }

        if (value.Kind == ValueKind.String && long.TryParse(value.Text,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long negative))
        {
            return unchecked((nuint)negative);
        }

        double rounded = Round(value, controlWord);
        return rounded < 0
            ? unchecked((nuint)checked((long)rounded))
            : checked((nuint)rounded);
    }

    private static double Round(Value value, ushort controlWord)
    {
        double number = NumericOperations.RuntimeNumber(value);
        return (controlWord & 0x0c00) switch
        {
            0x0400 => Math.Floor(number),
            0x0800 => Math.Ceiling(number),
            0x0c00 => Math.Truncate(number),
            _ => Math.Round(number, MidpointRounding.ToEven)
        };
    }

    private static NativeDllExtendedArgument Extended(Value value,
        ushort controlWord)
    {
        double number = NumericOperations.RuntimeNumber(value);
        int mode = controlWord & 0x0c00;
        if (mode == 0)
        {
            return new(number);
        }

        object? scalar = value.GetUnchangedNativeScalar();
        if (value.NativeVariantType == 6 && scalar is decimal currency)
        {
            return new(RoundCurrency(currency, number, mode));
        }

        Int128? exactInteger = scalar switch
        {
            long signed => (Int128)signed,
            ulong unsigned => (Int128)unsigned,
            _ => null
        };
        if (exactInteger is not { } exact)
        {
            return new(number);
        }

        // 64ビット整数の最近接値が元の型の範囲を超える場合も比較できます。
        var rounded = (Int128)number;
        number = mode switch
        {
            0x0400 when rounded > exact => Math.BitDecrement(number),
            0x0800 when rounded < exact => Math.BitIncrement(number),
            0x0c00 when exact > 0 && rounded > exact =>
                Math.BitDecrement(number),
            0x0c00 when exact < 0 && rounded < exact =>
                Math.BitIncrement(number),
            _ => number
        };
        return new(number);
    }

    private static double RoundCurrency(decimal currency, double number,
        int mode)
    {
        if (number == 0)
        {
            return number;
        }

        const int scale = 10000;
        long units = decimal.ToInt64(currency * scale);
        int shift = 52 - Math.ILogB(number);
        // VT_CYの整数部を拡張し、倍精度の仮数と余りを求めます。
        Int128 numerator = (Int128)units << shift;
        if (Int128.Abs(numerator) < (Int128)scale << 52)
        {
            // 最近接値が2の累乗へ繰り上がる境界では、元の指数に合わせます。
            numerator <<= 1;
            shift++;
        }

        Int128 significand = numerator / scale;
        Int128 remainder = numerator % scale;
        significand = mode switch
        {
            0x0400 when remainder < 0 => significand - 1,
            0x0800 when remainder > 0 => significand + 1,
            _ => significand
        };
        return Math.ScaleB((double)significand, -shift);
    }

    private static float Single(Value value)
    {
        float result = (float)NumericOperations.RuntimeNumber(value);
        if (!float.IsFinite(result))
        {
            throw new OverflowException("DLLのFLOAT引数が範囲外です。");
        }

        return result;
    }
}

internal readonly record struct NativeDllExtendedArgument(double Number);
