namespace UWSCSharp.Core;

/// <summary>
/// 語彙式を順に評価し、配列の由来に応じて登録語を作ります。
/// </summary>
internal static class SpeechRecognitionWordEvaluation
{
    internal static async Task<IEnumerable<string>> EvaluateAsync(
        IReadOnlyList<Expr> expressions, IEvaluationContext context,
        Func<Value, CancellationToken, ValueTask<string>> format,
        bool preserveSpecialCharacters, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(format);
        List<string> words = [];
        foreach (Expr expression in expressions)
        {
            token.ThrowIfCancellationRequested();
            (Value value, bool internalArray) = await EvaluateSourceAsync(
                expression, context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            token.ThrowIfCancellationRequested();
            IEnumerable<Value> items = SelectItems(value, internalArray);
            foreach (Value item in items)
            {
                token.ThrowIfCancellationRequested();
                string text = await format(item, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                token.ThrowIfCancellationRequested();
                words.Add(internalArray
                    ? SpeechRecognitionSpecialCharacters.Expand(text, preserveSpecialCharacters)
                    : text);
            }
        }

        return words;
    }

    private static async ValueTask<(Value Value, bool InternalArray)> EvaluateSourceAsync(
        Expr expression, IEvaluationContext context, CancellationToken token)
    {
        if (expression is NameExpr name)
        {
            Value named = context.Read(name.Name);
            return (named, IsInternalArray(named));
        }

        if (expression is IndexExpr index && context.Read(index.Name).Kind == ValueKind.Array)
        {
            int[] coordinates = await index.ResolveAsync(context, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            // 添字式が変数を更新する場合に備え、通常の添字評価と同じく再取得します。
            UwscArray array = context.Read(index.Name).AsArray();
            return (array.Get(coordinates),
                array.NativeElementType is null && coordinates.Length < array.Rank);
        }

        return (await expression.EvaluateAsync(context, token)
            .ConfigureAwait(WorkerThreadContext.IsCurrent), false);
    }

    private static bool IsInternalArray(Value value) =>
        value.Kind == ValueKind.Array && value.AsArray().NativeElementType is null;

    private static IEnumerable<Value> SelectItems(Value value, bool internalArray)
    {
        if (value.Kind != ValueKind.Array)
        {
            return [value];
        }

        UwscArray array = value.AsArray();
        if (internalArray)
        {
            return array.Values;
        }

        if (array.NativeElementType is not null and not 12)
        {
            // 原版の配列展開はVT_ARRAY | VT_VARIANTだけを選びます。
            return [value];
        }

        if (array.Rank > 2)
        {
            throw new NotSupportedException("3次元以上のVariant配列の認識語展開は未対応です。");
        }

        // Valuesは各軸の下限から始まる行優先の並びで、要素を再帰展開しません。
        return array.Values;
    }
}
