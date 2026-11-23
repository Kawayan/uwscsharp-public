namespace UWSCSharp.Core;

/// <summary>
/// 共通引数処理の数値判定と符号付き64ビットへの切捨てを扱います。
/// </summary>
internal static class SpeechRecognitionFlag
{
    internal static bool Read(string text)
    {
        if (!NumericOperations.TryParse(text, out double number))
        {
            throw new InvalidOperationException("音声認識のフラグを数値に変換できません。");
        }

        double integer = Math.Truncate(number);
        // long.MaxValueをdoubleへ変換すると2^63になるため、上限は排他的に検査します。
        if (integer < -9223372036854775808d || integer >= 9223372036854775808d)
        {
            throw new InvalidOperationException("音声認識のフラグが64ビット整数の範囲外です。");
        }

        return integer != 0;
    }

    internal static void CheckCall(string function, IReadOnlyList<Expr> arguments)
    {
        if (arguments.Count > 37 || arguments.Any(argument => argument is OmittedExpr))
        {
            throw new InvalidOperationException(
                $"{function}の引数は37個までです。途中の引数は省略できません。");
        }
    }
}
