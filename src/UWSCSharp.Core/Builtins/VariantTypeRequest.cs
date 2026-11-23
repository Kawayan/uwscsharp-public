namespace UWSCSharp.Core;

/// <summary>
/// 型照会と、省略された変換先を区別します。
/// </summary>
internal sealed record VariantTypeRequest(Value Source, ushort? Target)
{
    internal static VariantTypeRequest Read(IReadOnlyList<Value> values)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.VarType, values, 1, 2);
        if (values[0].Kind is ValueKind.Omitted or ValueKind.HashTable)
        {
            throw new InvalidOperationException("VARTYPEには通常の値を指定してください。");
        }

        if (values[0].Kind == ValueKind.Number)
        {
            _ = values[0].AsNumber();
        }

        if (values.Count < 2 || values[1].Kind == ValueKind.Omitted)
        {
            return new(values[0], null);
        }

        int type = NumericOperations.Integer(values[1]);
        if (type is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11
            or 12 or 13 or 16 or 17 or 18 or 19 or 20 or 21 or 256 or 258)
            && !HasSameType(values[0], type))
        {
            throw new InvalidOperationException("VARTYPEの変換先の型が不正です。");
        }

        return new(values[0], (ushort)type);
    }

    internal static bool HasSameType(Value value, int type) => Query(value).Number == type;

    internal static Value Query(Value value)
    {
        int type = value.Kind switch
        {
            ValueKind.Empty => 0,
            ValueKind.Null => 1,
            ValueKind.Nothing or ValueKind.Object => value.NativeVariantType is 9 or 13
                ? value.NativeVariantType.Value : 9,
            ValueKind.Error => 10,
            ValueKind.Number when value.IsLogical && value.Number is 0 or 1 => 11,
            ValueKind.Number => value.GetUnchangedNativeScalar() is not null
                && value.NativeVariantType is ushort nativeType ? nativeType : 5,
            ValueKind.String => value.ByteText is { } bytes && bytes.Text == value.Text ? 256 : 258,
            ValueKind.Array => 8192 | (value.AsArray().NativeElementType ?? 12),
            _ => throw new InvalidOperationException("VARTYPEで照会できない値です。")
        };
        bool unchanged = value.Kind switch
        {
            ValueKind.Number => value.GetUnchangedNativeScalar() is not null,
            ValueKind.String => value.NativeScalar is string original
                ? original == value.Text : value.NativeScalar is null && value.Text.Length == 0,
            _ => true
        };
        if (unchanged && value.NativeReferenceType is ushort referenceType)
        {
            type = referenceType;
        }

        return Value.Numeric(type) with
        {
            NativeVariantType = 18,
            NativeScalar = checked((ushort)type)
        };
    }
}
