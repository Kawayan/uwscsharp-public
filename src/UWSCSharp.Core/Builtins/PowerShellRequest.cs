namespace UWSCSharp.Core;

/// <summary>
/// PowerShellのコマンドと待機・表示指定を保持します。
/// </summary>
internal sealed record PowerShellRequest(string Command, bool NoWait, int Display)
{
    internal bool CaptureOutput => !NoWait && Display == 0;

    internal static PowerShellRequest Read(IReadOnlyList<Value> values)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.PowerShell, values, 1, 3);
        var arguments = new CallArguments(values);
        Value command = arguments.Get(0);
        if (command.Kind != ValueKind.String || command.Text.Contains('\0')
            || command.Text.Length > 32766)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.PowerShell}にはNULを含まない32766文字以内のコマンド文字列を指定してください。");
        }

        int display = arguments.Int(2);
        if (arguments.Get(2).NativeVariantType == 11
            && arguments.Get(2).GetUnchangedNativeScalar() is bool flag)
        {
            display = flag ? 1 : 0;
        }

        if (display is not (0 or 2))
        {
            display = 1;
        }

        return new(command.Text, arguments.Int(1) != 0, display);
    }
}
