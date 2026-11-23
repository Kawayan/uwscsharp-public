using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

/// <summary>
/// UWSC互換の配列操作を提供します。
/// </summary>
public static class ArrayOperations
{
    /// <summary>
    /// 文字列を指定した区切り文字で分割し、配列を返します。
    /// </summary>
    public static UwscArray Split(string text,
        string separator = " ", bool skipEmpty = false,
        bool numeric = false)
    {
        if (text.Length == 0)
        {
            return UwscArray.FromValues([]);
        }

        if (separator.Length == 0)
        {
            separator = " ";
        }

        return UwscArray.FromValues(SplitValues(
            text, separator, skipEmpty, numeric));
    }

    private static IEnumerable<Value> SplitValues(string text,
        string separator, bool skipEmpty, bool numeric)
    {
        int start = 0;
        while (true)
        {
            int found = StringOperations.FindForwardMatch(
                text, separator, start, StringComparison.Ordinal);
            int end = found < 0 ? text.Length : found;
            string part = text[start..end];
            Value value = numeric && TrySplitNumber(part, out double number)
                ? Value.Numeric(number) : Value.String(numeric ? "" : part);
            if (!skipEmpty || value.Kind != ValueKind.String
                || value.Text.Length != 0)
            {
                // 除外後の要素だけを渡し、配列の共通上限で停止します。
                yield return value;
            }

            if (found < 0)
            {
                yield break;
            }

            start = found + separator.Length;
        }
    }

    private static bool TrySplitNumber(string text, out double number)
    {
        if (NumericOperations.TryFiniteText(text, out number))
        {
            return true;
        }

        // SPLITだけで厳密な3桁区切りを許可し、変換は共通規則へ戻します。
        if (!Regex.IsMatch(text,
            @"\A[ \t\r\n\v\f]*[+-]?[0-9]{1,3}(?:,[0-9]{3})+(?:\.[0-9]*)?(?:[Ee][+-]?[0-9]+)?[ \t\r\n\v\f]*\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            return false;
        }

        return NumericOperations.TryFiniteText(
            text.Replace(",", "", StringComparison.Ordinal), out number);
    }

    // 添字と宣言上限では空文字を0とし、通常の数値変換とは区別します。
    internal static int Coordinate(Value input) =>
        input.Kind == ValueKind.String && input.Text.Length == 0
            ? 0 : NumericOperations.Integer(input);

    internal static int RangeIndex(Value input) => (int)Math.Clamp(
        Math.Truncate(NumericOperations.RuntimeNumber(input)),
        int.MinValue, int.MaxValue);

    internal static int ResizeIndex(Value input)
    {
        double upperBound = Math.Truncate(NumericOperations.RuntimeNumber(input));
        if (upperBound < int.MinValue || upperBound > int.MaxValue)
        {
            throw new InvalidOperationException("配列の上限が整数範囲外です。");
        }

        return (int)upperBound;
    }

    private static IEnumerable<Value> Range(UwscArray array,
        int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, array.Count - 1);
        for (int i = start; i <= end; i++)
        {
            yield return array.Values[i];
        }
    }

    /// <summary>
    /// 配列の要素を区切り文字で連結します。
    /// </summary>
    public static string Join(UwscArray array,
        string separator = " ", bool skipEmpty = false,
        int start = 0, int end = int.MaxValue)
    {
        var texts = new List<string>();
        long length = 0;
        foreach (Value value in Range(array, start, end))
        {
            string text = BuiltinText.Convert(value);
            if (skipEmpty && text.Length == 0)
            {
                continue;
            }

            if (texts.Count > 0)
            {
                length += separator.Length;
            }

            length += text.Length;
            if (length > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "JOINの出力長が文字列の上限を超えました。");
            }

            texts.Add(text);
        }

        if (texts.Count < 2)
        {
            return texts.Count == 0 ? "" : texts[0];
        }

        // 変換と長さ検査を済ませてから、返値の領域を一度だけ確保します。
        return string.Create((int)length,
            (Texts: texts, Separator: separator), static (destination, state) =>
            {
                int offset = 0;
                for (int index = 0; index < state.Texts.Count; index++)
                {
                    if (index > 0)
                    {
                        state.Separator.AsSpan().CopyTo(destination[offset..]);
                        offset += state.Separator.Length;
                    }

                    string text = state.Texts[index];
                    text.AsSpan().CopyTo(destination[offset..]);
                    offset += text.Length;
                }
            });
    }

    /// <summary>
    /// 指定範囲の要素から独立した配列を作成します。
    /// </summary>
    public static UwscArray Slice(UwscArray array,
        int start = 0,
        int end = int.MaxValue) => UwscArray.FromValues(Range(
        array, start, end));

    /// <summary>
    /// 指定範囲を指定値で埋めた配列を返します。
    /// </summary>
    public static UwscArray Fill(UwscArray array,
        Value value) => Fill(array, value, CancellationToken.None);

    internal static UwscArray Fill(UwscArray array, Value value,
        CancellationToken token) => array.WithValues(
            Enumerable.Repeat(value, array.Count), token);

    /// <summary>
    /// 要素を移動した新しい配列を返します。
    /// </summary>
    public static UwscArray Shift(UwscArray array,
        int shift) => Shift(array, shift, CancellationToken.None);

    internal static UwscArray Shift(UwscArray array, int shift,
        CancellationToken token) => array.WithValues(Enumerable.Range(0,
        array.Count).Select(i => (long)i - shift is >= 0
        && (long)i - shift < array.Count
        ? array.Values[(int)((long)i - shift)] : Value.Empty), token);

    /// <summary>
    /// 指定範囲の要素をモードに従って集計します。
    /// </summary>
    public static Value Calculate(UwscArray array, int mode,
        int start = 0, int end = int.MaxValue)
    {
        if (mode is < 1 or > 4)
        {
            throw new InvalidOperationException(
                "CALCARRAYの計算法が不正です。");
        }

        double result = 0;
        int count = 0;
        foreach (Value value in Range(array, start, end))
        {
            if (value.Kind == ValueKind.Empty)
            {
                continue;
            }

            double number = NumericOperations.RuntimeNumber(value);
            count++;
            result = mode switch
            {
                1 => result + number,
                2 => count == 1 ? number : Math.Min(result, number),
                3 => count == 1 ? number : Math.Max(result, number),
                _ => count == 1 ? number : UpdateAverage(result, number, count)
            };
            if (!double.IsFinite(result))
            {
                throw new InvalidOperationException(
                    "CALCARRAYの計算結果が有限範囲を超えました。");
            }
        }

        return count == 0 && mode != 1 ? Value.Empty : Value.Numeric(result);
    }

    private static double UpdateAverage(double average, double number, int count)
    {
        // 同符号では差、異符号では重み付きの各項を使い、和や差のオーバーフローを避けます。
        return Math.Sign(average) == Math.Sign(number)
            ? average + (number - average) / count
            : average * ((count - 1d) / count) + number / count;
    }

    internal static int SortMode(Value input)
    {
        double mode = Math.Truncate(NumericOperations.RuntimeNumber(input));
        if (mode is < 0 or > 5)
        {
            throw new InvalidOperationException("QSORTの順列は0～5で指定してください。");
        }

        return (int)mode;
    }

    /// <summary>
    /// 取得済みの値をソートし、引数の順序で結果を返します。
    /// </summary>
    public static IReadOnlyList<UwscArray> Sort(
        IReadOnlyList<UwscArray> arrays, int mode = 0) => Sort(arrays, mode, default);

    /// <summary>
    /// 取消を確認しながらキーと連動配列を同じ順序へ並べ替えます。
    /// </summary>
    public static IReadOnlyList<UwscArray> Sort(
        IReadOnlyList<UwscArray> arrays, int mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arrays.Count is < 1 or > 9)
        {
            throw new InvalidOperationException(
                "QSORTは1～9配列に対応しています。");
        }

        if (mode is < 0 or > 5)
        {
            throw new InvalidOperationException(
                "QSORTの順列は0～5で指定してください。");
        }

        if (mode >= 2 && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "QSORTのUnicode比較・自然順はWindowsで使用できます。");
        }

        UwscArray keys = arrays[0];
        int count = keys.Count;
        while (count > 0 && keys.Values[count
            - 1].Kind == ValueKind.Empty)
        {
            if ((count & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            count--;
        }

        foreach (UwscArray array in arrays)
        {
            if (array.Count < count)
            {
                throw new InvalidOperationException("連動配列の要素数が有効キー数より少なくなっています。");
            }
        }

        var prepared = new SortKey[count];
        for (int index = 0; index < count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Value value = keys.Values[index];
            prepared[index] = mode >= 2
                ? new SortKey(true, 0, BuiltinText.Convert(value))
                : value.Kind switch
                {
                    ValueKind.String => new SortKey(true, 0, value.Text),
                    ValueKind.Empty => new SortKey(false, 0, null),
                    ValueKind.Number => new SortKey(false, NumericOperations.RuntimeNumber(value), null),
                    _ => throw new InvalidOperationException("QSORTのキーは文字列・有限数値・EMPTYで指定してください。")
                };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (count < 2)
        {
            return [.. arrays];
        }

        int[] order = [.. Enumerable.Range(0, count)];
        int Compare(SortKey a, SortKey b)
        {
            if (mode >= 2)
            {
                return QSortStringComparison.Compare(a.Text!, b.Text!, mode >= 4);
            }

            if (a.IsText != b.IsText)
            {
                return a.IsText ? 1 : -1;
            }

            return a.IsText ? StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text)
                : a.Number.CompareTo(b.Number);
        }
        var pending = new Stack<(int Left, int Right)>();
        pending.Push((0, count - 1));
        int comparisons = 0;
        int CheckedCompare(SortKey left, SortKey right)
        {
            if ((comparisons++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Compare(left, right);
        }

        while (pending.TryPop(out (int Left, int Right) region))
        {
            int left = region.Left;
            int right = region.Right;
            SortKey pivot = prepared[order[left + (right - left) / 2]];
            while (left <= right)
            {
                while (left <= region.Right && CheckedCompare(
                    prepared[order[left]], pivot) < 0)
                {
                    left++;
                }

                while (right >= region.Left && CheckedCompare(
                    prepared[order[right]], pivot) > 0)
                {
                    right--;
                }

                if (left > right)
                {
                    break;
                }

                (order[left], order[right]) = (order[right], order[left]);

                left++;
                right--;
            }

            if (region.Left < right)
            {
                pending.Push((region.Left, right));
            }

            if (left < region.Right)
            {
                pending.Push((left, region.Right));
            }
        }

        if (mode is 1 or 3 or 5)
        {
            System.Array.Reverse(order);
        }

        IEnumerable<Value> Reorder(UwscArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                yield return array.Values[index < count ? order[index] : index];
            }
        }

        IReadOnlyList<UwscArray> result = [.. arrays.Select(array =>
            array.WithValues(Reorder(array), cancellationToken))];
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private readonly record struct SortKey(bool IsText, double Number, string? Text);
}
