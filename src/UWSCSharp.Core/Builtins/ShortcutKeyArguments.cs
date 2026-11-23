namespace UWSCSharp.Core;

/// <summary>
/// ショートカットの対象IDと送信キーを副作用の前に検査します。
/// </summary>
internal sealed record ShortcutKeyArguments(int WindowId,
    IReadOnlyList<int> Keys)
{
    internal static ShortcutKeyArguments Read(IReadOnlyList<Value> arguments)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.ScKey,
            arguments, 2, int.MaxValue);
        int windowId = NumericOperations.Integer(arguments[0]);
        var keys = new List<int>();
        foreach (Value argument in arguments.Skip(1))
        {
            if (argument.Kind == ValueKind.Omitted)
            {
                throw new InvalidOperationException(
                    "SCKEYのキーは省略できません。");
            }

            int key = NumericOperations.Integer(argument);
            if (key < 0 || key > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "SCKEYのキーが範囲外です。");
            }

            keys.Add(key);
        }

        return new ShortcutKeyArguments(windowId, keys.AsReadOnly());
    }
}
