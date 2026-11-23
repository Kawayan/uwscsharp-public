namespace UWSCSharp.Core;

internal static class Names
{
    private static readonly HashSet<string> Reserved = new(
        StringComparer.OrdinalIgnoreCase)
    {
        Keywords.True,
        Keywords.False,
        Keywords.Empty,
        Keywords.Null,
        Keywords.Nothing,
        Keywords.Dim,
        Keywords.Print,
        BuiltinFunctionNames.Sleep,
        Keywords.Exit,
        Keywords.ExitExit,
        Keywords.And,
        Keywords.Or,
        Keywords.Xor,
        Keywords.Mod,
        Keywords.If,
        Keywords.IfB,
        Keywords.Then,
        Keywords.Else,
        Keywords.ElseIf,
        Keywords.EndIf,
        Keywords.For,
        Keywords.To,
        Keywords.Step,
        Keywords.In,
        Keywords.Next,
        Keywords.While,
        Keywords.Wend,
        Keywords.Repeat,
        Keywords.Until,
        Keywords.Select,
        Keywords.Case,
        Keywords.Default,
        Keywords.SelEnd,
        Keywords.Call,
        Keywords.Break,
        Keywords.Continue,
        Keywords.Function,
        Keywords.Procedure,
        Keywords.Fend,
        Keywords.Public,
        Keywords.Const,
        Keywords.Option,
        Keywords.Try,
        Keywords.Except,
        Keywords.Finally,
        Keywords.EndTry,
        Keywords.ComErrIgn,
        Keywords.ComErrRet,
        BuiltinVariableNames.ComErrFlg,
        Keywords.Thread,
        Keywords.Module,
        Keywords.EndModule,
        Keywords.Class,
        Keywords.This,
        Keywords.Global,
        Keywords.EndClass,
        Keywords.With,
        Keywords.EndWith,
        Keywords.TextBlock,
        Keywords.EndTextBlock,
        Keywords.HashTbl,
        Keywords.DefDll,
        Keywords.Var,
    };

    /// <summary>
    /// 指定された名前と引数の妥当性を検証します。
    /// </summary>
    public static void Validate(string name, int line)
    {
        if (!CanDeclare(name))
        {
            throw new ScriptException(line,
                $"使用できない名前です: {name}");
        }
    }

    internal static bool CanDeclare(string name) =>
        IdentifierPatterns.Full().IsMatch(name)
        && !Reserved.Contains(name)
        && !BuiltinFunctions.IsBuiltin(name)
        && !BuiltinConstants.TryGet(name, out _);
}
