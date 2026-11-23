namespace UWSCSharp.Core;

/// <summary>
/// DOSCMDの省略、待機、表示と出力方式を保持します。
/// </summary>
internal sealed record DosCommandRequest(string Command, bool NoWait,
    bool ShowWindow, int OutputMode)
{
    internal bool UnicodeOutput => (OutputMode & 1) != 0;

    internal bool ReturnExitCode => (OutputMode & 2) != 0;

    internal bool CaptureOutput => !NoWait && !ShowWindow && !ReturnExitCode;

    internal static DosCommandRequest Read(IReadOnlyList<Value> values)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.DosCmd,
            values, 1, 4);
        var arguments = new CallArguments(values);
        Value command = arguments.Get(0);
        if (command.Kind != ValueKind.String || command.Text.Contains('\0')
            || command.Text.Length > 32766)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.DosCmd}にはNULを含まない32766文字以内のコマンド文字列を指定してください。");
        }

        int mode = arguments.Int(3);
        if (arguments.Get(3).NativeVariantType == 11
            && arguments.Get(3).GetUnchangedNativeScalar() is bool flag)
        {
            mode = flag ? 1 : 0;
        }

        int noWait = arguments.Int(1);
        if (noWait < 0)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.DosCmd}の非待機フラグは0以上で指定してください。");
        }

        // 原版と同様に空行を除き、各行の前後を除いて順番に実行します。
        string normalized = string.Join(" & ", command.Text.Split(
            ["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim()).Where(line => line.Length != 0));
        return new(normalized, noWait != 0,
            arguments.Int(2) != 0, mode);
    }
}
