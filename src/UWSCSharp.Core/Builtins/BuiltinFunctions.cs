using System.Text;

namespace UWSCSharp.Core;
/// <summary>
/// 副作用のない組み込み関数。参照先の更新と乱数の状態は実行サービスで管理します。
/// </summary>
public static class BuiltinFunctions
{
    internal readonly record struct Signature(int Min, int Max,
        bool ValidateBeforeEvaluation = false);

    private static readonly Dictionary<string, Signature> Signatures = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [BuiltinFunctionNames.LengthB] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.CopyB] = new(2, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.PosB] = new(2, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.AscB] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ChrB] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.StrConv] = new(2, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Encode] = new(2, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Decode] = new(2, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Copy] = new(2, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Pos] = new(2, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.BetweenStr] = new(1, 5, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Length] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.LengthS] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ChkNum] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Val] = new(1, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Replace] = new(3, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ChgMoj] = new(3, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Trim] = new(1, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Chr] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Asc] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.IsUnicode] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Format] = new(2, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Int] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Abs] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ZCut] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Round] = new(1, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Sqrt] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Power] = new(2, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Sin] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Cos] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Tan] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Exp] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Ln] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.LogN] = new(2, 2, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Ceil] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ArcSin] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ArcCos] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.ArcTan] = new(1, 1, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Split] = new(1, 4, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Join] = new(1, 5, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.Slice] = new(1, 3, ValidateBeforeEvaluation: true),
        [BuiltinFunctionNames.CalcArray] = new(2, 4, ValidateBeforeEvaluation: true)
    };

    internal static bool ValidatesBeforeEvaluation(string name) =>
        Signatures.TryGetValue(name, out Signature signature)
            && signature.ValidateBeforeEvaluation;

    internal static Signature GetSignature(string name) => Signatures[name];

    /// <summary>
    /// 副作用のない組み込み関数かどうかを判定します。
    /// </summary>
    public static bool IsPure(
        string name) => Signatures.ContainsKey(name);

    /// <summary>
    /// 組み込み関数として定義されているかどうかを判定します。
    /// </summary>
    public static bool IsBuiltin(string name) =>
        FileFunctions.IsBuiltin(name)
        || WindowsFunctions.IsBuiltin(name)
        || IsPure(name)
        || StatefulBuiltins.IsBuiltin(name);

    /// <summary>
    /// 引数の数と必須引数の指定を検証します。
    /// </summary>
    public static void CheckArguments(string name,
        IReadOnlyList<Value> arguments, int minimum, int maximum)
    {
        if (arguments.Count < minimum || arguments.Count
            > maximum || arguments.Take(minimum).Any(v => v.Kind
            == ValueKind.Omitted))
        {
            throw new InvalidOperationException(
                $"引数の数または省略位置が不正です: {name}");
        }
    }

    /// <summary>
    /// 指定した入力を評価し、結果を返します。
    /// </summary>
    public static Value Evaluate(string name,
        IReadOnlyList<Value> arguments, bool sameString = false) =>
        Evaluate(name, arguments, sameString, null);

    internal static Value Evaluate(string name,
        IReadOnlyList<Value> arguments, bool sameString,
        INumericBuiltinArithmetic? arithmetic)
    {
        if (!Signatures.TryGetValue(name,
            out Signature signature))
        {
            throw new InvalidOperationException(
                $"純粋関数として未対応です: {name}");
        }

        CheckArguments(name, arguments, signature.Min,
            signature.Max);
        var args = new CallArguments(arguments);
        Value N(double n) => Value.Numeric(n);
        switch (name.ToUpperInvariant())
        {
            case BuiltinFunctionNames.LengthB:
                return N(ByteTextOperations.Length(args.Get(0)));
            case BuiltinFunctionNames.CopyB:
                return ByteTextOperations.Copy(args.Get(0), args.Get(1), args.Get(2));
            case BuiltinFunctionNames.PosB:
                return N(ByteTextOperations.Position(args.Get(0), args.Get(1), args.Get(2), sameString));
            case BuiltinFunctionNames.AscB:
                return N(ByteTextOperations.Asc(args.Get(0)));
            case BuiltinFunctionNames.ChrB:
                return ByteTextOperations.Chr(args.Get(0));
            case BuiltinFunctionNames.StrConv:
                return Value.String(ByteTextOperations.ConvertText(args.Get(0), args.Get(1)));
            case BuiltinFunctionNames.Encode:
                return ByteTextOperations.Encode(args.Get(0), args.Get(1));
            case BuiltinFunctionNames.Decode:
                return ByteTextOperations.Decode(args.Get(0), args.Get(1));
            case BuiltinFunctionNames.BetweenStr:
                return BetweenOperations.Between(arguments, sameString);
            case BuiltinFunctionNames.Length:
                return N(LengthOperations.Length(args.Get(0)));
            case BuiltinFunctionNames.LengthS:
                return N(BuiltinText.Convert(args.Get(0)).Length);
            case BuiltinFunctionNames.Copy:
                return Value.String(CopyOperations.Copy(
                    args.Get(0), args.Get(1), args.Get(2)));
            case BuiltinFunctionNames.Pos:
                return N(PositionOperations.Position(args.Get(0),
                    args.Get(1), args.Get(2), sameString));
            case BuiltinFunctionNames.Trim:
                return Value.String(StringOperations.Trim(
                    BuiltinText.Convert(args.Get(0)),
                    Math.Truncate(NumericOperations.RuntimeNumber(args.Get(1))) != 0));
            case BuiltinFunctionNames.Replace:
            case BuiltinFunctionNames.ChgMoj:
                return Value.String(
                    StringOperations.Replace(BuiltinText.Convert(args.Get(0)),
                        BuiltinText.Convert(args.Get(1)), BuiltinText.Convert(args.Get(2)), sameString));
            case BuiltinFunctionNames.ChkNum:
                return Value.Logical(NumericOperations.TryFiniteText(args.Str(0),
                    out _));
            case BuiltinFunctionNames.Val:
                return EvaluateVal(args);
            case BuiltinFunctionNames.Chr:
                return EvaluateChr(args);
            case BuiltinFunctionNames.Asc:
                string text = BuiltinText.Convert(args.Get(0));
                return N(text.Length == 0 ? 0
                    : char.IsSurrogatePair(text, 0) ? char.ConvertToUtf32(text,
                    0) : text[0]);
            case BuiltinFunctionNames.IsUnicode:
                return EvaluateIsUnicode(args);
            case BuiltinFunctionNames.Format:
                return Value.String(FormatOperations.Format(
                    args.Get(0), args.Get(1), args.Get(2)));
            case BuiltinFunctionNames.Int:
                double truncateInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Truncate, truncateInput, 0)
                    ?? Math.Truncate(truncateInput));
            case BuiltinFunctionNames.Abs:
                double absoluteInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Abs, absoluteInput, 0)
                    ?? Math.Abs(absoluteInput));
            case BuiltinFunctionNames.ZCut:
                double zeroCutInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.ZeroCut, zeroCutInput, 0)
                    ?? Math.Max(0, zeroCutInput));
            case BuiltinFunctionNames.Round:
                return N(NumericOperations.Round(NumericOperations.RuntimeNumber(args.Get(0)),
                    NumericOperations.RoundDigits(args.Get(1))));
            case BuiltinFunctionNames.Ceil:
                double ceilingInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Ceiling, ceilingInput, 0)
                    ?? NumericOperations.Ceiling(ceilingInput));
            case BuiltinFunctionNames.Sqrt:
                return EvaluateSqrt(args, arithmetic);
            case BuiltinFunctionNames.Power:
                return EvaluatePower(args, arithmetic);
            case BuiltinFunctionNames.Sin:
                double sineInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Sin, sineInput, 0) ?? Math.Sin(sineInput));
            case BuiltinFunctionNames.Cos:
                double cosineInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Cos, cosineInput, 0) ?? Math.Cos(cosineInput));
            case BuiltinFunctionNames.Tan:
                double tangentInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Tan, tangentInput, 0) ?? Math.Tan(tangentInput));
            case BuiltinFunctionNames.Exp:
                double exponentialInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.Exp, exponentialInput, 0) ?? Math.Exp(exponentialInput));
            case BuiltinFunctionNames.Ln:
                return EvaluateLn(args, arithmetic);
            case BuiltinFunctionNames.LogN:
                return EvaluateLogN(args, arithmetic);
            case BuiltinFunctionNames.ArcSin:
                return EvaluateArcSin(args, arithmetic);
            case BuiltinFunctionNames.ArcCos:
                return EvaluateArcCos(args, arithmetic);
            case BuiltinFunctionNames.ArcTan:
                double arcTangentInput = NumericOperations.RuntimeNumber(args.Get(0));
                return N(arithmetic?.EvaluateBuiltin(
                    NumericBuiltinOperation.ArcTan, arcTangentInput, 0) ?? Math.Atan(arcTangentInput));
            case BuiltinFunctionNames.Split:
                return Value.FromArray(
                    ArrayOperations.Split(
                        BuiltinText.Convert(args.Get(0)),
                        BuiltinText.Convert(args.Get(1, Value.String(" "))),
                        Math.Truncate(NumericOperations.RuntimeNumber(args.Get(2))) != 0,
                        Math.Truncate(NumericOperations.RuntimeNumber(args.Get(3))) != 0));
            case BuiltinFunctionNames.Join:
                return Value.String(JoinOperations.Join(arguments));
            case BuiltinFunctionNames.Slice:
                return Value.FromArray(
                    ArrayOperations.Slice(args.Get(0).AsArray(),
                        ArrayOperations.RangeIndex(args.Get(1)),
                        ArrayOperations.RangeIndex(
                            args.Get(2, Value.Numeric(int.MaxValue)))));
            case BuiltinFunctionNames.CalcArray:
                return CalculateOperations.Calculate(arguments);
            default:
                throw new InvalidOperationException(
                    $"未対応の関数: {name}");
        }
    }

    private static Value EvaluateVal(CallArguments args)
    {
        Value valInput = args.Get(0);
        if (valInput.Kind == ValueKind.Number
            && double.IsFinite(valInput.Number))
        {
            return Value.Numeric(NumericOperations.RuntimeNumber(valInput));
        }

        return NumericOperations.TryFiniteText(args.Str(0),
            out double number) ? Value.Numeric(number) : Value.Numeric(
            NumericOperations.RuntimeNumber(args.Get(1, Value.Numeric(-999999))));
    }

    private static Value EvaluateChr(CallArguments args)
    {
        double characterCode = Math.Truncate(
            NumericOperations.RuntimeNumber(args.Get(0)));
        if (characterCode is < 0 or > 0x10ffff)
        {
            throw new InvalidOperationException(
                "CHRの文字コードが範囲外です。");
        }

        int code = (int)characterCode;
        return Value.String(code <= 0xffff
            ? ((char)code).ToString() : char.ConvertFromUtf32(code));
    }

    private static Value EvaluateIsUnicode(CallArguments args)
    {
        string unicodeText = BuiltinText.Convert(args.Get(0));
        try
        {
            ScriptEncodings.Ansi.GetByteCount(unicodeText);
            return Value.Logical(false);
        }
        catch (EncoderFallbackException)
        {
            return Value.Logical(true);
        }
    }

    private static Value EvaluateSqrt(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double radicand = NumericOperations.RuntimeNumber(args.Get(0));
        if (radicand < 0)
        {
            throw new InvalidOperationException(
                "SQRTの引数は0以上で指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.Sqrt, radicand, 0) ?? Math.Sqrt(radicand));
    }

    private static Value EvaluatePower(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double basis = NumericOperations.RuntimeNumber(args.Get(0));
        double exponent = NumericOperations.RuntimeNumber(args.Get(1));
        if (basis == 0 && exponent < 0)
        {
            throw new InvalidOperationException(
                "POWERで底が0の場合、指数は0以上で指定してください。");
        }

        if (basis < 0 && Math.Truncate(exponent) != exponent)
        {
            throw new InvalidOperationException(
                "POWERで負の底には整数の指数を指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.Power, basis, exponent)
            ?? Math.Pow(basis, exponent));
    }

    private static Value EvaluateLn(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double logarithmInput =
            NumericOperations.RuntimeNumber(args.Get(0));
        if (logarithmInput <= 0)
        {
            throw new InvalidOperationException(
                "LNの引数は0より大きい値で指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.Ln, logarithmInput, 0) ?? Math.Log(logarithmInput));
    }

    private static Value EvaluateLogN(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double logarithmBase =
            NumericOperations.RuntimeNumber(args.Get(0));
        double logarithmValue =
            NumericOperations.RuntimeNumber(args.Get(1));
        if (logarithmBase <= 0 || logarithmBase == 1)
        {
            throw new InvalidOperationException(
                "LOGNの底は0より大きく、1以外の値で指定してください。");
        }

        if (logarithmValue <= 0)
        {
            throw new InvalidOperationException(
                "LOGNの真数は0より大きい値で指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.LogN, logarithmBase, logarithmValue)
            ?? Math.Log(logarithmValue, logarithmBase));
    }

    private static Value EvaluateArcSin(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double arcSineInput =
            NumericOperations.RuntimeNumber(args.Get(0));
        if (arcSineInput is < -1 or > 1)
        {
            throw new InvalidOperationException(
                "ARCSINの引数は-1～1で指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.ArcSin, arcSineInput, 0)
            ?? Math.Asin(arcSineInput));
    }

    private static Value EvaluateArcCos(CallArguments args,
        INumericBuiltinArithmetic? arithmetic)
    {
        double arcCosineInput =
            NumericOperations.RuntimeNumber(args.Get(0));
        if (arcCosineInput is < -1 or > 1)
        {
            throw new InvalidOperationException(
                "ARCCOSの引数は-1～1で指定してください。");
        }

        return Value.Numeric(arithmetic?.EvaluateBuiltin(
            NumericBuiltinOperation.ArcCos, arcCosineInput, 0)
            ?? Math.Acos(arcCosineInput));
    }
}

internal static class BuiltinConstants
{
    private static readonly Dictionary<string, int> Values = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [BuiltinConstantNames.VarEmpty] = 0,
        [BuiltinConstantNames.VarNull] = 1,
        [BuiltinConstantNames.VarSmallInt] = 2,
        [BuiltinConstantNames.VarInteger] = 3,
        [BuiltinConstantNames.VarSingle] = 4,
        [BuiltinConstantNames.VarDouble] = 5,
        [BuiltinConstantNames.VarCurrency] = 6,
        [BuiltinConstantNames.VarDate] = 7,
        [BuiltinConstantNames.VarOleStr] = 8,
        [BuiltinConstantNames.VarDispatch] = 9,
        [BuiltinConstantNames.VarError] = 10,
        [BuiltinConstantNames.VarBoolean] = 11,
        [BuiltinConstantNames.VarVariant] = 12,
        [BuiltinConstantNames.VarUnknown] = 13,
        [BuiltinConstantNames.VarSByte] = 16,
        [BuiltinConstantNames.VarByte] = 17,
        [BuiltinConstantNames.VarWord] = 18,
        [BuiltinConstantNames.VarDWord] = 19,
        [BuiltinConstantNames.VarInt64] = 20,
        [BuiltinConstantNames.VarAStr] = 256,
        [BuiltinConstantNames.VarUStr] = 258,
        [BuiltinConstantNames.VarArray] = 8192,
        [BuiltinConstantNames.ModAlt] = 1,
        [BuiltinConstantNames.ModControl] = 2,
        [BuiltinConstantNames.ModShift] = 4,
        [BuiltinConstantNames.ModWin] = 8,
        [BuiltinConstantNames.ErrorValue] = -999999,
        [BuiltinConstantNames.CalcAdd] = 1,
        [BuiltinConstantNames.CalcMin] = 2,
        [BuiltinConstantNames.CalcMax] = 3,
        [BuiltinConstantNames.CalcAverage] = 4,
        [BuiltinConstantNames.QSortAscending] = 0,
        [BuiltinConstantNames.QSortDescending] = 1,
        [BuiltinConstantNames.QSortUnicodeAscending] = 2,
        [BuiltinConstantNames.QSortUnicodeDescending] = 3,
        [BuiltinConstantNames.QSortNaturalAscending] = 4,
        [BuiltinConstantNames.QSortNaturalDescending] = 5,
        [BuiltinConstantNames.FileExists] = (int)ScriptFileMode.Exists,
        [BuiltinConstantNames.FileRead] = (int)ScriptFileMode.Read,
        [BuiltinConstantNames.FileWrite] = (int)ScriptFileMode.Write,
        [BuiltinConstantNames.FileWriteAnsi] = (int)ScriptFileMode.WriteAnsi,
        [BuiltinConstantNames.FileWriteUtf8] = (int)ScriptFileMode.WriteUtf8,
        [BuiltinConstantNames.FileWriteUtf8Bom] = (int)ScriptFileMode.WriteUtf8Bom,
        [BuiltinConstantNames.FileWriteUtf16] = (int)ScriptFileMode.WriteUtf16,
        [BuiltinConstantNames.FileNoCr] = (int)ScriptFileMode.NoCr,
        [BuiltinConstantNames.FileTab] = (int)ScriptFileMode.Tab,
        [BuiltinConstantNames.FileExclusive] = (int)ScriptFileMode.Exclusive,
        [BuiltinConstantNames.FileLineCount] = -1,
        [BuiltinConstantNames.FileAllText] = -2,
        [BuiltinConstantNames.FileInsert] = -1
    };

    /// <summary>
    /// 名前に対応する組み込み定数の取得を試みます。
    /// </summary>
    public static bool TryGet(string name, out Value value)
    {
        if (BuiltinWindowSelectors.TryGet(name, out string selector))
        {
            value = Value.String(selector);
            return true;
        }

        bool found = Values.TryGetValue(name, out int n)
            || HashConstants.Values.TryGetValue(name, out n)
            || WindowsConstants.Values.TryGetValue(name, out n);
        value = Value.Numeric(n);
        return found;
    }
}
