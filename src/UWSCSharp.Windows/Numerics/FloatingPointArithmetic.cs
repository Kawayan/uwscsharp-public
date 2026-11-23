namespace UWSCSharp.Windows;

internal enum FloatingPointOperation
{
    Add,
    Subtract,
    Multiply,
    Divide
}

internal enum FloatingPointBuiltinOperation
{
    Sqrt,
    Sin,
    Cos,
    Tan,
    Exp,
    Ln,
    ArcTan,
    Truncate,
    Abs,
    LogN,
    ArcSin,
    ArcCos,
    Ceiling,
    ZeroCut,
    Power
}

/// <summary>
/// native側の状態コードと、コピー済みの演算結果を保持します。
/// </summary>
internal readonly record struct FloatingPointResult(
    uint Code, double Value, uint FaultState)
{
    internal bool Succeeded => Code == 0;

    internal ushort FaultControlWord => (ushort)FaultState;

    internal ushort FaultStatusWord => (ushort)(FaultState >> 16);
}

internal interface IFloatingPointArithmetic : IDisposable
{
    ushort ReadControlWord();

    FloatingPointResult Evaluate(ushort control,
        FloatingPointOperation operation, double left, double right);
}

internal interface IFloatingPointBuiltins : IFloatingPointArithmetic
{
    FloatingPointResult EvaluateBuiltin(ushort control,
        FloatingPointBuiltinOperation operation, double left, double right);
}

internal static class FloatingPointCleanup
{
    internal static void Release(IDisposable owner, Exception primary)
    {
        try
        {
            owner.Dispose();
        }
        catch (Exception cleanup)
        {
            const string key = "UWSCSharp.FloatingPointCleanup";
            primary.Data[key] = primary.Data[key] is Exception previous
                ? new AggregateException(previous, cleanup) : cleanup;
        }
    }
}
