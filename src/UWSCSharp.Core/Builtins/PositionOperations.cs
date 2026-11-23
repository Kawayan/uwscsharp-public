namespace UWSCSharp.Core;

/// <summary>
/// POSの文字列変換と有限回数の境界処理を提供します。
/// </summary>
internal static class PositionOperations
{
    internal static int Position(Value needleValue, Value sourceValue,
        Value occurrenceValue, bool sameString)
    {
        string needle = BuiltinText.Convert(needleValue);
        string source = BuiltinText.Convert(sourceValue);
        double occurrence = occurrenceValue.Kind is ValueKind.Empty or ValueKind.Omitted
            ? 1 : Math.Truncate(NumericOperations.RuntimeNumber(occurrenceValue));
        if (Math.Abs(occurrence) > source.Length)
        {
            return 0;
        }

        return StringOperations.Position(needle, source,
            (int)occurrence, sameString);
    }
}
