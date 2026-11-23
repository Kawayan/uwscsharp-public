namespace UWSCSharp.Core;

/// <summary>
/// COPYの型変換と位置・長さの境界処理を提供します。
/// </summary>
internal static class CopyOperations
{
    internal static string Copy(Value input, Value start, Value count)
    {
        string text = BuiltinText.Convert(input);
        int position = Index(start);
        int? length = count.Kind is ValueKind.Empty or ValueKind.Omitted
            ? null : Index(count);
        return StringOperations.Copy(text, position, length);
    }

    private static int Index(Value value)
    {
        double number = Math.Truncate(NumericOperations.RuntimeNumber(value));
        // 文字列の長さを超える指定は、整数変換前に境界へ収めます。
        return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
    }
}
