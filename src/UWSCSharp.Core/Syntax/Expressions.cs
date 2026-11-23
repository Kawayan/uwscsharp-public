namespace UWSCSharp.Core;

internal interface IEvaluationContext
{
    bool SameString => false;

    INumericArithmetic? NumericArithmetic => null;

    Value ReadWithTarget() => throw new InvalidOperationException(
        "先頭ドットはWITH内で使用してください。");

    Value Read(string name);

    void Write(string name, Value value) => throw new InvalidOperationException(
        "この評価環境ではHASHTBLの削除を使用できません。");

    ValueTask<Value> InvokeAsync(CallExpr call,
        CancellationToken token);

    bool IsCoreMemberTarget(Value target) => false;

    ValueTask<Value> InvokeCoreMemberAsync(Value target,
        MemberExpr member, Expr? assignment, CancellationToken token) =>
        throw new NotSupportedException(
            "この評価環境ではモジュールの操作を使用できません。");

    ValueTask<Value> AssignEvaluatedAsync(Expr target, Expr expression,
        CancellationToken token) => throw new InvalidOperationException(
            "この評価環境では動的な代入を使用できません。");

    ValueTask<Value> InvokeMemberAsync(Value target, string name,
        IReadOnlyList<Value> arguments, Value? assigned,
        CancellationToken token) => throw new NotSupportedException(
            "この評価環境ではobjectの操作を使用できません。");

    ValueTask<Value> InvokeReferenceMemberAsync(MemberExpr member, Expr? assignment,
        CancellationToken token) => throw new NotSupportedException(
            "この評価環境ではCOMのVAR/名前付き引数を使用できません。");
}

internal sealed record EvaluatedAssignmentExpr(Expr Target,
    Expr Expression) : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token) =>
        context.AssignEvaluatedAsync(Target, Expression, token);
}

internal sealed record MemberExpr(Expr Target, string Name,
    IReadOnlyList<Expr> Arguments) : Expr
{
    internal bool Indexed
    {
        get; init;
    }

    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token) =>
        InvokeAsync(context, null, token);

    public async ValueTask<Value> InvokeAsync(IEvaluationContext context,
        Expr? assignment, CancellationToken token)
    {
        Value target = await Target.EvaluateAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        if (context.IsCoreMemberTarget(target))
        {
            return await context.InvokeCoreMemberAsync(target, this,
                assignment, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        if (Indexed)
        {
            throw new InvalidOperationException(
                "この添字構文はモジュールの配列に指定してください。");
        }

        if (Arguments.Any(argument => argument is ReferenceExpr or NamedExpr))
        {
            MemberExpr resolved = this with
            {
                Target = new LiteralExpr(target)
            };
            return await context.InvokeReferenceMemberAsync(resolved,
                assignment, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        var arguments = new List<Value>();
        foreach (Expr argument in Arguments)
        {
            arguments.Add(await argument.EvaluateAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent));
        }

        Value? assigned = assignment is null ? null
            : await assignment.EvaluateAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        return await context.InvokeMemberAsync(target, Name, arguments,
            assigned, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
    }
}

internal sealed record NamedExpr(string Name, Expr Argument) : Expr
{
    public override ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token) => Argument.EvaluateAsync(context, token);
}

internal sealed record ReferenceExpr(Expr Target) : Expr
{
    public override ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token) => Target.EvaluateAsync(context, token);
}

internal sealed record AssignedMemberExpr(MemberExpr Member, Expr? Assigned) : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token) =>
        Member.InvokeAsync(context, Assigned, token);
}

internal abstract record Expr
{
    public abstract ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token);
}

internal sealed record WithTargetExpr : Expr
{
    public override ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.ReadWithTarget());
    }
}

internal sealed record GroupedExpr(Expr Inner) : Expr
{
    public override ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token) => Inner.EvaluateAsync(context, token);
}

internal sealed record LiteralExpr(Value Value) : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Value);
    }
}

internal sealed record NameExpr(string Name) : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.Read(Name));
    }
}

internal sealed record OmittedExpr : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context,
        CancellationToken token) => ValueTask.FromResult(new Value(
        ValueKind.Omitted));
}

internal sealed record IndexExpr(string Name,
    IReadOnlyList<Expr> Indices) : Expr
{
    public async ValueTask<Value[]> ResolveValuesAsync(
        IEvaluationContext context, CancellationToken token)
    {
        var values = new Value[Indices.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = await Indices[i].EvaluateAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        return values;
    }

    public async ValueTask<int[]> ResolveAsync(
        IEvaluationContext context, CancellationToken token)
    {
        int[] indices = new int[Indices.Count];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = ArrayOperations.Coordinate(
                await Indices[i].EvaluateAsync(context,
                token).ConfigureAwait(WorkerThreadContext.IsCurrent));
        }

        return indices;
    }

    public override async ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token)
    {
        if (context.Read(Name).Kind == ValueKind.HashTable)
        {
            Value[] hashIndices = await ResolveValuesAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            UwscHashTable table = context.Read(Name).AsHashTable();
            if (hashIndices.Length == 0)
            {
                return Value.FromHashTable(table);
            }

            if (hashIndices.Length > 2)
            {
                throw new InvalidOperationException("HASHTBLの添字が多すぎます。");
            }

            if (hashIndices.Length == 1)
            {
                return table.Get(hashIndices[0].Display());
            }

            int mode = NumericOperations.Integer(hashIndices[1]);
            if (mode is HashConstants.Key or HashConstants.Val)
            {
                if (hashIndices[0].Kind == ValueKind.Empty)
                {
                    throw new InvalidOperationException(
                        "HASHTBLの順列番号にEMPTYは指定できません。");
                }

                string key = table.KeyAt(NumericOperations.Integer(hashIndices[0]));
                return mode == HashConstants.Key ? Value.String(key) : table.Get(key);
            }

            string name = hashIndices[0].Display();
            bool exists = table.Contains(name);
            if (mode == HashConstants.Remove)
            {
                context.Write(Name, Value.FromHashTable(table.Remove(name)));
            }
            else if (mode != HashConstants.Exists)
            {
                throw new InvalidOperationException("HASHTBLの読出し指定が不正です。");
            }

            return Value.Logical(exists);
        }

        int[] indices = await ResolveAsync(context,
            token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        return context.Read(Name).AsArray().Get(indices);
    }
}

internal sealed record HashCreateExpr(Expr? Flags) : Expr
{
    public override async ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token) => Value.FromHashTable(UwscHashTable.Create(
            Flags is null ? 0 : NumericOperations.Integer(
                await Flags.EvaluateAsync(context, token).ConfigureAwait(WorkerThreadContext.IsCurrent))));
}

internal sealed record UnaryExpr(string Operator,
    Expr Operand) : Expr
{
    public override async ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token)
    {
        Value value = await Operand.EvaluateAsync(context,
            token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        return Apply(value);
    }

    internal Value Apply(Value value)
    {
        // 単項＋は型を保持し、負号と0比較だけで数値変換します。
        if (Operator == "+")
        {
            return value;
        }

        double n = NumericOperations.Number(value);
        return Operator == "!" ? Value.Logical(n == 0)
            : Value.Numeric(Operator == "-" ? -n : n);
    }
}

internal sealed record BinaryExpr(string Operator,
    Expr Left, Expr Right) : Expr
{
    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context, CancellationToken token) =>
        BinaryExpressionEvaluator.EvaluateAsync(this, context, token,
            shortCircuit: false);
}

internal sealed record CallExpr(string Name,
    IReadOnlyList<Expr> Arguments) : Expr
{
    internal bool PreserveSpecialCharacters
    {
        get; init;
    }

    public override ValueTask<Value> EvaluateAsync(
        IEvaluationContext context,
        CancellationToken token) => context.InvokeAsync(this, token);
}
