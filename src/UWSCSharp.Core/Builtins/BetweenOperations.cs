namespace UWSCSharp.Core;

/// <summary>
/// BETWEENSTRの有限変換と両方向の区間検索を提供します。
/// </summary>
internal static class BetweenOperations
{
    internal static Value Between(IReadOnlyList<Value> arguments, bool sameString)
    {
        var args = new CallArguments(arguments);
        string text = BuiltinText.Convert(args.Get(0));
        string before = BuiltinText.Convert(args.Get(1));
        string after = BuiltinText.Convert(args.Get(2));
        double occurrence = Math.Truncate(NumericOperations.RuntimeNumber(
            args.Get(3, Value.Numeric(1))));
        bool countFromStart = Math.Truncate(
            NumericOperations.RuntimeNumber(args.Get(4))) != 0;
        bool reverse = occurrence < 0;
        double absoluteCount = occurrence == 0 ? 1 : Math.Abs(occurrence);
        if (absoluteCount > (long)text.Length + 1)
        {
            return Value.Empty;
        }

        long count = (long)absoluteCount;
        StringComparison comparison = sameString
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int cursor = reverse ? text.Length : 0;
        for (long index = 0; index < count; index++)
        {
            if (index > 0 && cursor == (reverse ? 0 : text.Length))
            {
                return Value.Empty;
            }

            int start;
            int end;
            int next;
            if (reverse)
            {
                end = after.Length == 0 ? cursor
                    : StringOperations.FindBackwardMatch(text, after, cursor, comparison);
                if (end < 0)
                {
                    return Value.Empty;
                }

                int left = before.Length == 0 ? 0
                    : StringOperations.FindBackwardMatch(text, before, end, comparison);
                if (left < 0)
                {
                    return Value.Empty;
                }

                start = left + before.Length;
                next = countFromStart ? end : left;
            }
            else
            {
                int left = before.Length == 0 ? cursor
                    : StringOperations.FindForwardMatch(text, before, cursor, comparison);
                if (left < 0)
                {
                    return Value.Empty;
                }

                start = left + before.Length;
                end = after.Length == 0 ? text.Length
                    : StringOperations.FindForwardMatch(text, after, start, comparison);
                if (end < 0)
                {
                    return Value.Empty;
                }

                next = countFromStart ? start : end + after.Length;
            }

            if (index == count - 1)
            {
                return Value.String(text[start..end]);
            }

            // 同じ位置で数え続けず、検索方向へ進めないときは不成立にします。
            if (reverse ? next >= cursor : next <= cursor)
            {
                return Value.Empty;
            }

            cursor = next;
        }

        return Value.Empty;
    }
}
