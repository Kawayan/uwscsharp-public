namespace UWSCSharp.Core;

/// <summary>
/// 省略を考慮して関数引数を読み出します。
/// </summary>
internal readonly struct CallArguments(IReadOnlyList<Value> values)
{
    internal IReadOnlyList<Value> Values => values;

    internal bool Has(int index) => index < values.Count
        && values[index].Kind != ValueKind.Omitted;

    internal Value Get(int index, Value fallback = default) =>
        Has(index) ? values[index] : fallback;

    internal int Int(int index, int fallback = 0) => Has(index)
        ? NumericOperations.Integer(values[index]) : fallback;

    internal string Str(int index, string fallback = "") =>
        Has(index) ? values[index].Display() : fallback;
}
