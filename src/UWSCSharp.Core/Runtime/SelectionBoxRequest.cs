using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// 選択画面の引数と戻り値の規則を保持します。
/// </summary>
internal sealed record SelectionBoxRequest(int Kind, double TimeoutSeconds,
    string Message, IReadOnlyList<string> Items, int? X, int? Y, int ReturnMode)
{
    internal static SelectionBoxRequest Read(IReadOnlyList<Value> arguments,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Count < 3 || arguments[0].Kind == ValueKind.Omitted
            || arguments[1].Kind == ValueKind.Omitted)
        {
            throw new InvalidOperationException("SLCTBOXには種別、秒数とメッセージ・項目を指定してください。");
        }

        int flags = NumericOperations.Integer(arguments[0]);
        int kind = flags & 31;
        if (kind == 0)
        {
            kind = 1;
        }
        int result = flags & 192;
        if ((flags & ~223) != 0 || (kind & (kind - 1)) != 0 || result == 192)
        {
            throw new InvalidOperationException("SLCTBOXは選択方式を1つ、STR/NUMはどちらか1つ指定してください。");
        }

        double timeout = NumericOperations.RuntimeNumber(arguments[1]);
        if (timeout > int.MaxValue)
        {
            throw new InvalidOperationException("SLCTBOXの秒数が範囲外です。");
        }

        int start = 2;
        int? x = null;
        int? y = null;
        if (arguments.Count >= 4 && arguments[2].Kind == ValueKind.Number
            && arguments[3].Kind == ValueKind.Number)
        {
            x = NumericOperations.Integer(arguments[2]);
            y = NumericOperations.Integer(arguments[3]);
            start = 4;
        }

        List<string> text = [];
        for (int index = start; index < arguments.Count; index++)
        {
            Flatten(arguments[index], text, 0, token);
        }

        if (text.Count == 0)
        {
            throw new InvalidOperationException("SLCTBOXのメッセージを指定してください。");
        }

        IEnumerable<string> items = text.Skip(1);
        if (result == 0)
        {
            items = items.Take(31);
        }

        token.ThrowIfCancellationRequested();
        return new(kind, Math.Max(0, timeout), text[0], [.. items], x, y, result);
    }

    internal Value Selected(IEnumerable<int> indices)
    {
        int[] selected = [.. indices.Distinct().Order()];
        if (selected.Any(index => index < 0 || index >= Items.Count))
        {
            throw new InvalidOperationException("選択項目の位置が範囲外です。");
        }

        if (ReturnMode == 64)
        {
            return Value.String(string.Join("\t", selected.Select(index => Items[index])));
        }

        if (ReturnMode == 128)
        {
            return selected.Length == 1 ? IntegerResult(selected[0])
                : Value.String(string.Join("\t", selected.Select(index =>
                    index.ToString(CultureInfo.InvariantCulture))));
        }

        int bits = 0;
        foreach (int index in selected)
        {
            if (index > 30)
            {
                throw new InvalidOperationException("ビット形式の選択位置は0～30です。");
            }

            bits |= 1 << index;
        }

        return IntegerResult(bits);
    }

    internal Value TimedOut => ReturnMode == 0 ? IntegerResult(0) : Value.String("");

    internal static Value Closed => IntegerResult(-1);

    private static Value IntegerResult(int number) =>
        Value.Numeric(number) with
        {
            NativeVariantType = 3,
            NativeScalar = number
        };

    private static void Flatten(Value value, List<string> text, int depth,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 16 || text.Count >= UwscArray.MaximumElements + 1)
        {
            throw new InvalidOperationException("SLCTBOXの項目数または配列の深さが範囲外です。");
        }

        if (value.Kind == ValueKind.Array)
        {
            foreach (Value item in value.AsArray().Values)
            {
                Flatten(item, text, depth + 1, token);
            }
        }
        else
        {
            text.Add(value.Kind == ValueKind.Omitted ? "" : BuiltinText.Convert(value));
        }
    }
}
