namespace UWSCSharp.Core;

/// <summary>
/// 入力画面の本文・既定文字列・表示指定を保持します。
/// </summary>
internal sealed record InputBoxRequest(
    string Message, string DefaultText, bool Password, int? X, int? Y)
{
    internal static InputBoxRequest Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count is < 1 or > 5 || arguments[0].Kind == ValueKind.Omitted)
        {
            throw new InvalidOperationException("INPUTにはメッセージと任意の4引数を指定してください。");
        }

        var args = new CallArguments(arguments);
        string initial = args.Has(1) ? BuiltinText.Convert(args.Get(1)) : "";
        if (initial.Contains('\0'))
        {
            throw new InvalidOperationException("INPUTの既定文字列にNULは指定できません。");
        }

        int? x = args.Has(3) ? NumericOperations.Integer(args.Get(3)) : null;
        int? y = args.Has(4) ? NumericOperations.Integer(args.Get(4)) : null;
        return new(BuiltinText.Convert(args.Get(0)), initial,
            args.Has(2) && NumericOperations.RuntimeNumber(args.Get(2)) != 0,
            x.HasValue && y.HasValue ? x : null,
            x.HasValue && y.HasValue ? y : null);
    }
}
