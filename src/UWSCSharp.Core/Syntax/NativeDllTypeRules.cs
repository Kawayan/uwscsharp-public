namespace UWSCSharp.Core;

/// <summary>
/// 静的解析で根拠を確認した禁止条件だけを検査します。
/// </summary>
internal static class NativeDllTypeRules
{
    // この検査の通過は、ABIや全ての型の組合せへの対応を保証しません。
    internal static void ValidateKnownConstraints(NativeDllDeclaration declaration,
        int line, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        token.ThrowIfCancellationRequested();
        for (int index = 0; index < declaration.Parameters.Count; index++)
        {
            ValidateType(declaration.Parameters[index], $"引数{index + 1}",
                line, 0, true, token);
        }

        if (declaration.ReturnType is not null)
        {
            ValidateType(declaration.ReturnType, "戻り型", line, 0, false, token);
        }
    }

    private static void ValidateType(NativeDllType type, string location,
        int line, int depth, bool isParameter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth >= ScriptLimits.MaxExpressionNesting)
        {
            throw new ScriptException(line,
                $"{Keywords.DefDll}: {location}の型の入れ子が深すぎます。");
        }

        if (type is NativeDllScalarType scalar && scalar.IsByReference
            && scalar.Primitive is NativeDllPrimitive.Char
                or NativeDllPrimitive.Wchar or NativeDllPrimitive.Void)
        {
            string name = scalar.Primitive.ToString().ToUpperInvariant();
            throw new ScriptException(line,
                $"{Keywords.DefDll}: {location}の{name}に{Keywords.Var}は指定できません。");
        }

        if (type is NativeDllStructureType structure)
        {
            if (isParameter && structure.IsByReference)
            {
                throw new ScriptException(line,
                    $"{Keywords.DefDll}: {location}の構造体に{Keywords.Var}は指定できません。");
            }

            if (isParameter && structure.IsArray)
            {
                throw new ScriptException(line,
                    $"{Keywords.DefDll}: {location}の構造体に配列は指定できません。");
            }

            for (int index = 0; index < structure.Fields.Count; index++)
            {
                ValidateType(structure.Fields[index],
                    $"{location}のフィールド{index + 1}", line, depth + 1,
                    isParameter, token);
            }
        }

        // 原版の宣言時の配列検査は引数側で確認した条件です。
        if (isParameter && type is NativeDllScalarType array && array.IsArray
            && array.Primitive is NativeDllPrimitive.String or NativeDllPrimitive.Pchar
                or NativeDllPrimitive.Pbyte or NativeDllPrimitive.Wstring
                or NativeDllPrimitive.Pwchar or NativeDllPrimitive.Char
                or NativeDllPrimitive.Wchar or NativeDllPrimitive.Void
                or NativeDllPrimitive.Variant or NativeDllPrimitive.SafeArray)
        {
            string name = array.Primitive.ToString().ToUpperInvariant();
            throw new ScriptException(line,
                $"{Keywords.DefDll}: {location}の{name}に配列は指定できません。");
        }
    }
}
