namespace UWSCSharp.Core;

/// <summary>
/// LENGTHの文字列・配列・連想配列の件数を求めます。
/// </summary>
internal static class LengthOperations
{
    internal static int Length(Value input) => input.Kind switch
    {
        ValueKind.HashTable => input.AsHashTable().Count,
        ValueKind.Array => input.AsArray().GetLength(0),
        _ => StringOperations.Length(BuiltinText.Convert(input))
    };
}
