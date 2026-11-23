using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// CW所有者を内部の数値評価へ接続します。
/// </summary>
internal sealed class FloatingPointArithmeticBridge(
    FloatingPointEnvironment environment)
    : IForkableNumericArithmetic, INumericControlArithmetic,
        INumericBuiltinArithmetic, INumericArithmeticScope
{
    public bool IsActive => environment.IsActive;

    /// <summary>
    /// 整数引数の変換に使う現在の制御語を取得します。
    /// </summary>
    public ushort? ActiveControlWord => environment.ActiveControlWord;

    public void SetControlWord(ushort control)
    {
        try
        {
            environment.SetControlWord(control);
        }
        catch (Exception error) when (error is DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException
            or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "浮動小数点演算用のDLLを使用できません。", error);
        }
    }

    public INumericArithmeticScope ForkForThread() =>
        new ThreadArithmetic(environment.ForkForThread());

    public double Evaluate(NumericBinaryOperation operation, double left, double right)
    {
        FloatingPointOperation native = operation switch
        {
            NumericBinaryOperation.Add => FloatingPointOperation.Add,
            NumericBinaryOperation.Subtract => FloatingPointOperation.Subtract,
            NumericBinaryOperation.Multiply => FloatingPointOperation.Multiply,
            NumericBinaryOperation.Divide => FloatingPointOperation.Divide,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return ResultValue(environment.Evaluate(native, left, right));
    }

    public double EvaluateBuiltin(NumericBuiltinOperation operation,
        double left, double right)
    {
        FloatingPointBuiltinOperation native = operation switch
        {
            NumericBuiltinOperation.Sqrt => FloatingPointBuiltinOperation.Sqrt,
            NumericBuiltinOperation.Sin => FloatingPointBuiltinOperation.Sin,
            NumericBuiltinOperation.Cos => FloatingPointBuiltinOperation.Cos,
            NumericBuiltinOperation.Tan => FloatingPointBuiltinOperation.Tan,
            NumericBuiltinOperation.Exp => FloatingPointBuiltinOperation.Exp,
            NumericBuiltinOperation.Ln => FloatingPointBuiltinOperation.Ln,
            NumericBuiltinOperation.ArcTan => FloatingPointBuiltinOperation.ArcTan,
            NumericBuiltinOperation.Truncate => FloatingPointBuiltinOperation.Truncate,
            NumericBuiltinOperation.Abs => FloatingPointBuiltinOperation.Abs,
            NumericBuiltinOperation.LogN => FloatingPointBuiltinOperation.LogN,
            NumericBuiltinOperation.ArcSin => FloatingPointBuiltinOperation.ArcSin,
            NumericBuiltinOperation.ArcCos => FloatingPointBuiltinOperation.ArcCos,
            NumericBuiltinOperation.Ceiling => FloatingPointBuiltinOperation.Ceiling,
            NumericBuiltinOperation.ZeroCut => FloatingPointBuiltinOperation.ZeroCut,
            NumericBuiltinOperation.Power => FloatingPointBuiltinOperation.Power,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return ResultValue(environment.EvaluateBuiltin(native, left, right));
    }

    public void Dispose() => environment.Dispose();

    private static double ResultValue(FloatingPointResult result)
    {
        if (!result.Succeeded)
        {
            var failure = new InvalidOperationException(
                $"浮動小数点演算に失敗しました: 0x{result.Code:x8}");
            failure.Data["UWSCSharp.FloatingPointCode"] = result.Code;
            failure.Data["UWSCSharp.FloatingPointState"] = result.FaultState;
            throw failure;
        }

        return result.Value;
    }

    private sealed class ThreadArithmetic(FloatingPointEnvironment owner)
        : INumericArithmeticScope, INumericControlArithmetic,
            INumericBuiltinArithmetic
    {
        private readonly FloatingPointArithmeticBridge bridge = new(owner);

        public bool IsActive => owner.IsActive;

        public ushort? ActiveControlWord => owner.ActiveControlWord;

        public void SetControlWord(ushort control) => bridge.SetControlWord(control);

        public double Evaluate(NumericBinaryOperation operation,
            double left, double right) => bridge.Evaluate(operation, left, right);

        public double EvaluateBuiltin(NumericBuiltinOperation operation,
            double left, double right) => bridge.EvaluateBuiltin(operation, left, right);

        public void Dispose() => owner.Dispose();
    }
}
