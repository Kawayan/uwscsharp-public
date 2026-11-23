namespace UWSCSharp.Core;

/// <summary>
/// 表示するメッセージと省略を保持するMSGBOXの要求です。
/// </summary>
internal sealed record MessageBoxRequest(
    string Message, int Buttons, int? X, int? Y, int FocusButton, bool Links)
{
    private static readonly int[] ButtonOrder = [4, 8, 1, 2, 16, 32, 64];

    internal int DefaultButton => FocusButton != 0 && (Buttons & FocusButton) != 0
        ? FocusButton : ButtonOrder.First(button => (Buttons & button) != 0);

    internal IEnumerable<int> VisibleButtons => ButtonOrder.Where(button => (Buttons & button) != 0);

    internal static MessageBoxRequest Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count is < 1 or > 6 || arguments[0].Kind == ValueKind.Omitted)
        {
            throw new InvalidOperationException("MSGBOXにはメッセージと任意の5引数を指定してください。");
        }

        var args = new CallArguments(arguments);
        int buttons = args.Has(1) ? NumericOperations.Integer(args.Get(1)) : 1;
        if (buttons == 0)
        {
            buttons = 1;
        }

        int focus = args.Has(4) ? NumericOperations.Integer(args.Get(4)) : 0;
        if (buttons < 0 || (buttons & ~127) != 0
            || focus < 0 || (focus & ~127) != 0 || (focus != 0 && (focus & (focus - 1)) != 0))
        {
            throw new InvalidOperationException("MSGBOXのボタン指定が不正です。");
        }

        return new(BuiltinText.Convert(args.Get(0)), buttons,
            args.Has(2) ? NumericOperations.Integer(args.Get(2)) : null,
            args.Has(3) ? NumericOperations.Integer(args.Get(3)) : null,
            focus, args.Has(5) && NumericOperations.RuntimeNumber(args.Get(5)) != 0);
    }
}
