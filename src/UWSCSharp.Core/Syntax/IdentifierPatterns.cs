using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

/// <summary>
/// 識別子と関数宣言の字句パターンを共有します。
/// </summary>
internal static partial class IdentifierPatterns
{
    private const string Identifier = @"[\p{L}_][\p{L}\p{N}_]*";

    [GeneratedRegex("^" + Identifier + "$")]
    internal static partial Regex Full();

    [GeneratedRegex("^" + Identifier)]
    internal static partial Regex Prefix();

    [GeneratedRegex("^(" + Identifier + @")\s*\((.*)\)$")]
    internal static partial Regex Routine();

    [GeneratedRegex("^(" + Identifier + @")\s*((?:\[\s*\]\s*)+)$")]
    internal static partial Regex ArrayParameter();
}
