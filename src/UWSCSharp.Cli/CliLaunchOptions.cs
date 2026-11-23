namespace UWSCSharp.Cli;

/// <summary>
/// CLI専用の先頭オプションと、スクリプトへ渡す引数を分離します。
/// </summary>
internal sealed record CliLaunchOptions(bool ManualCleanup, string[] ScriptArguments)
{
    internal const string CleanupPrefix = "--input-cleanup=";
    internal const string ManualCleanupOption = CleanupPrefix + "manual";

    internal static CliLaunchOptions Parse(IReadOnlyList<string> arguments, bool supportsManual)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException("スクリプトのパスが必要です。");
        }

        bool manual = arguments[0].StartsWith(CleanupPrefix, StringComparison.Ordinal);
        if (manual && (arguments[0] != ManualCleanupOption || !supportsManual))
        {
            throw new ArgumentException("この起動形式では入力フックの手動回収を指定できません。");
        }

        int first = manual ? 1 : 0;
        if (arguments.Count == first)
        {
            throw new ArgumentException("スクリプトのパスが必要です。");
        }

        return new CliLaunchOptions(manual, [.. arguments.Skip(first)]);
    }
}
