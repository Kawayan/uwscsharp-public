namespace UWSCSharp.Core;

/// <summary>
/// 許可した変数と数値関数だけを使う、上限付きの数値式です。
/// </summary>
public sealed class CompiledNumericExpression
{
    private const int MaximumCharacters = 4096;
    private const int MaximumNodes = 1024;
    private const int MaximumTokens = 1024;
    private const int MaximumDepth = 32;
    private readonly Expr expression;

    private static readonly HashSet<string> Functions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        BuiltinFunctionNames.Int, BuiltinFunctionNames.Abs,
        BuiltinFunctionNames.ZCut, BuiltinFunctionNames.Round,
        BuiltinFunctionNames.Sqrt, BuiltinFunctionNames.Power,
        BuiltinFunctionNames.Sin, BuiltinFunctionNames.Cos,
        BuiltinFunctionNames.Tan, BuiltinFunctionNames.Exp,
        BuiltinFunctionNames.Ln, BuiltinFunctionNames.LogN,
        BuiltinFunctionNames.Ceil, BuiltinFunctionNames.ArcSin,
        BuiltinFunctionNames.ArcCos, BuiltinFunctionNames.ArcTan
    };

    private CompiledNumericExpression(Expr expression)
    {
        this.expression = expression;
    }

    /// <summary>
    /// 式全体を検査し、再利用できる数値式を作ります。
    /// </summary>
    public static CompiledNumericExpression Compile(string source,
        IEnumerable<string> variableNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variableNames);
        if (source.Length > MaximumCharacters)
        {
            throw new InvalidOperationException("数値式の原文は4096文字までです。");
        }

        var names = new HashSet<string>(variableNames,
            StringComparer.OrdinalIgnoreCase);
        Expr root = new Expression(source, preserveGrouping: true,
            maximumTokens: MaximumTokens)
            .ParseExpression();
        var pending = new Stack<(Expr Node, int Depth)>();
        pending.Push((root, 1));
        int count = 0;
        while (pending.TryPop(out (Expr Node, int Depth) current))
        {
            if (++count > MaximumNodes || current.Depth > MaximumDepth)
            {
                throw new InvalidOperationException("数値式の要素数または深さが上限を超えています。");
            }

            int nextDepth = current.Depth + 1;
            switch (current.Node)
            {
                case LiteralExpr literal when literal.Value.Kind == ValueKind.Number:
                    _ = literal.Value.AsNumber();
                    break;
                case NameExpr name when names.Contains(name.Name):
                    break;
                case GroupedExpr grouped:
                    pending.Push((grouped.Inner, nextDepth));
                    break;
                case UnaryExpr unary:
                    pending.Push((unary.Operand, nextDepth));
                    break;
                case BinaryExpr binary:
                    pending.Push((binary.Left, nextDepth));
                    pending.Push((binary.Right, nextDepth));
                    break;
                case CallExpr call when Functions.Contains(call.Name):
                    BuiltinFunctions.Signature signature =
                        BuiltinFunctions.GetSignature(call.Name);
                    if (call.Arguments.Count < signature.Min
                        || call.Arguments.Count > signature.Max)
                    {
                        throw new InvalidOperationException("数値関数の引数の数が不正です。");
                    }

                    foreach (Expr argument in call.Arguments)
                    {
                        pending.Push((argument, nextDepth));
                    }

                    break;
                default:
                    throw new InvalidOperationException("数値式で許可していない要素があります。");
            }
        }

        return new CompiledNumericExpression(root);
    }

    /// <summary>
    /// 数値変数のスナップショットで評価し、有限の数値を返します。
    /// </summary>
    public double Evaluate(IReadOnlyDictionary<string, double> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var snapshot = new Dictionary<string, Value>(
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> pair in variables)
        {
            if (!double.IsFinite(pair.Value))
            {
                throw new InvalidOperationException("数値式の変数は有限数値で指定してください。");
            }

            snapshot.Add(pair.Key, Value.Numeric(pair.Value));
        }

        Value result = expression.EvaluateAsync(
            new NumericContext(snapshot), default).GetAwaiter().GetResult();
        return result.AsNumber();
    }

    private sealed class NumericContext(
        IReadOnlyDictionary<string, Value> variables) : IEvaluationContext
    {
        public Value Read(string name) => variables.TryGetValue(name,
            out Value value) ? value : throw new InvalidOperationException(
                $"数値式の変数が指定されていません: {name}");

        public ValueTask<Value> InvokeAsync(CallExpr call,
            CancellationToken token) =>
            BuiltinInvocation.EvaluateValidatedAsync(call, this, token);
    }
}
