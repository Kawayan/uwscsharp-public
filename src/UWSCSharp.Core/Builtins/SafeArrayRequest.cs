namespace UWSCSharp.Core;

/// <summary>
/// SAFEARRAYの上下限、要素型と確保前の要素数を保持します。
/// </summary>
internal sealed record SafeArrayRequest(IReadOnlyList<int> LowerBounds,
    IReadOnlyList<int> UpperBounds, ushort ElementType, int ElementCount)
{
    internal static SafeArrayRequest Read(IReadOnlyList<Value> values)
    {
        ValidatePositions(values.Count, index => values[index].Kind == ValueKind.Omitted);
        int[] lower = values.Count >= 4 ? new int[2] : new int[1];
        int[] upper = new int[lower.Length];
        lower[0] = values.Count == 0 ? 1 : ReadInteger(values[0]);
        upper[0] = values.Count < 2 || values[1].Kind == ValueKind.Omitted
            ? 0 : ReadInteger(values[1]);
        if (lower.Length == 2)
        {
            lower[1] = ReadInteger(values[2]);
            upper[1] = ReadInteger(values[3]);
        }
        int typeIndex = values.Count == 3 ? 2 : values.Count == 5 ? 4 : -1;
        int type = typeIndex < 0 || values[typeIndex].Kind == ValueKind.Omitted
            ? 12 : ReadInteger(values[typeIndex]);
        if (type is not (2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11
            or 12 or 13 or 16 or 17 or 18 or 19))
        {
            throw new InvalidOperationException("SAFEARRAYの要素型には対応していません。");
        }
        int count = UwscArray.ValidateBounds(lower, upper);
        return new(Array.AsReadOnly(lower), Array.AsReadOnly(upper),
            (ushort)type, count);
    }

    /// <summary>
    /// 値を評価せず、引数の個数と省略位置を検査します。
    /// </summary>
    internal static void ValidatePositions(int count, Func<int, bool> omitted)
    {
        if (count is < 0 or > 5
            || (count > 0 && omitted(0))
            || (count >= 4 && (omitted(2) || omitted(3))))
        {
            throw new InvalidOperationException(
                $"引数の数または省略位置が不正です: {BuiltinFunctionNames.SafeArray}");
        }
    }

    private static int ReadInteger(Value value)
    {
        if (value.Kind == ValueKind.Empty)
        {
            throw new InvalidOperationException("SAFEARRAYの数値にEMPTYは指定できません。");
        }
        // 元のVT_BOOLを保ち、COMのTRUEは-1として整数化します。
        return NumericOperations.Integer(
            Value.Numeric(NumericOperations.RuntimeNumber(value)));
    }
}
