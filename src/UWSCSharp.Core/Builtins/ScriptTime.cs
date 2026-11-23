using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// GETTIMEの採用範囲の日時計算を提供します。
/// </summary>
internal static class ScriptTime
{
    internal static DateTime Evaluate(IReadOnlyList<Value> arguments,
        TimeProvider clock)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.GetTime, arguments, 0, 2);
        double days = arguments.Count == 0
            || arguments[0].Kind == ValueKind.Omitted ? 0
            : NumericOperations.RuntimeNumber(arguments[0]);
        string basis = arguments.Count > 1
            && arguments[1].Kind != ValueKind.Omitted
            ? arguments[1].Display() : "";
        DateTime date;
        if (basis.Length > 0)
        {
            string text = basis
                .Replace("/", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace(":", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);
            string[] formats = ["yyyyMMdd", "yyyyMMddHHmmss"];
            if (!DateTime.TryParseExact(text, formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                throw new InvalidOperationException(
                    "GETTIMEの基準日が不正です。");
            }
        }
        else
        {
            date = clock.GetLocalNow().DateTime;
        }

        try
        {
            return date.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                "GETTIMEの計算結果が日時範囲外です。");
        }
    }

    internal static Dictionary<string, Value> Variables(DateTime date)
    {
        var values = new Dictionary<string, Value>(
            StringComparer.OrdinalIgnoreCase);
        string[] names = ["YY", "MM", "DD", "HH", "NN", "SS", "ZZ", "WW"];
        int[] numbers = [date.Year, date.Month, date.Day, date.Hour,
            date.Minute, date.Second, date.Millisecond, (int)date.DayOfWeek];
        for (int i = 0; i < names.Length; i++)
        {
            values["G_TIME_" + names[i]] = Value.Numeric(numbers[i]);
            if (i < 7)
            {
                int number = i == 0 ? numbers[i] % 100 : numbers[i];
                values["G_TIME_" + names[i] + "2"] = Value.String(
                    number.ToString(i == 6 ? "D3" : "D2",
                        CultureInfo.InvariantCulture));
            }
        }

        values["G_TIME_YY4"] = Value.String(date.Year.ToString(
            "D4", CultureInfo.InvariantCulture));
        return values;
    }
}
