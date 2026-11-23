namespace UWSCSharp.Core;

internal enum NumericBinaryOperation
{
    Add,
    Subtract,
    Multiply,
    Divide
}

/// <summary>
/// 実行が借用する数値演算です。所有と解放は呼出し側が管理します。
/// </summary>
/// <remarks>主体を分けない部品は並行呼出しへ対応してください。</remarks>
internal interface INumericArithmetic
{
    double Evaluate(NumericBinaryOperation operation, double left, double right);
}

internal enum NumericBuiltinOperation
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
/// 現在主体の制御語を使い、数値組込み関数を評価します。
/// </summary>
internal interface INumericBuiltinArithmetic : INumericArithmetic
{
    double EvaluateBuiltin(NumericBuiltinOperation operation,
        double left, double right);
}

/// <summary>
/// 現在主体と新規主体向けのCWを変更します。
/// </summary>
internal interface INumericControlArithmetic : INumericArithmetic
{
    bool IsActive => true;
    ushort? ActiveControlWord => null;


    void SetControlWord(ushort control);
}

/// <summary>
/// workerの処理開始時に、その主体が所有する演算部品を作ります。
/// </summary>
/// <remarks>入れ子のTHREADも、実行設定で借用した同じ生成元を使います。</remarks>
internal interface IForkableNumericArithmetic : INumericArithmetic
{
    INumericArithmeticScope ForkForThread();
}

/// <summary>
/// 通常実行が所有する演算部品を作ります。
/// </summary>
internal interface INumericArithmeticProvider
{
    INumericArithmeticScope CreateNumericArithmetic();
}

/// <summary>
/// 実行主体が所有し、終了時に解放する演算部品です。
/// </summary>
internal interface INumericArithmeticScope : INumericArithmetic, IDisposable
{
}
