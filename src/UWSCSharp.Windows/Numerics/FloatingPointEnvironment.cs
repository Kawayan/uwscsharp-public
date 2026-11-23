namespace UWSCSharp.Windows;

/// <summary>
/// 物理threadの設定を残さず、主体ごとの現在CWと共有の既定CWを保持します。
/// </summary>
internal sealed class FloatingPointEnvironment : IDisposable
{
    private readonly Domain domain;
    private readonly bool rootOwner;
    private ushort? currentControl;
    private bool disposed;

    /// <summary>
    /// 演算部品の所有を引き取り、初期化失敗時も解放します。
    /// </summary>
    internal FloatingPointEnvironment(IFloatingPointArithmetic arithmetic)
    {
        ArgumentNullException.ThrowIfNull(arithmetic);
        domain = new Domain(() => arithmetic, startManaged: false);
        domain.Initialize();
        currentControl = domain.DefaultControl = domain.InitialControl;
        rootOwner = true;
    }

    /// <summary>
    /// 最初の非ゼロCW指定までDLLを開かない実行の所有者を作ります。
    /// </summary>
    internal FloatingPointEnvironment(Func<IFloatingPointArithmetic> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        domain = new Domain(load, startManaged: true);
        rootOwner = true;
    }

    private FloatingPointEnvironment(Domain domain, ushort? control)
    {
        this.domain = domain;
        currentControl = control;
    }

    internal ushort CurrentControlWord
    {
        get
        {
            lock (domain.Gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return RequireControlWord();
            }
        }
    }

    internal bool IsActive
    {
        get
        {
            lock (domain.Gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return currentControl.HasValue;
            }
        }
    }

    internal ushort? ActiveControlWord
    {
        get
        {
            lock (domain.Gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return currentControl;
            }
        }
    }

    internal void SetControlWord(ushort control)
    {
        lock (domain.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (control != 0)
            {
                domain.Initialize();
                currentControl = control;
            }
            else
            {
                currentControl = domain.StartManaged ? null : domain.InitialControl;
            }

            domain.DefaultControl = currentControl;
        }
    }

    /// <summary>
    /// 呼出し時点の共有既定CWで、独立した現在CWの主体を作ります。
    /// </summary>
    internal FloatingPointEnvironment ForkForThread()
    {
        lock (domain.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed || domain.Closing, this);
            var child = new FloatingPointEnvironment(domain, domain.DefaultControl);
            domain.Owners++;
            return child;
        }
    }

    internal FloatingPointResult Evaluate(FloatingPointOperation operation,
        double left, double right)
    {
        lock (domain.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ushort control = RequireControlWord();
            return domain.Arithmetic!.Evaluate(control, operation, left, right);
        }
    }

    /// <summary>
    /// 現在主体のCWを指定して、対応する組込み関数を評価します。
    /// </summary>
    internal FloatingPointResult EvaluateBuiltin(FloatingPointBuiltinOperation operation,
        double left, double right)
    {
        lock (domain.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ushort control = RequireControlWord();
            if (domain.Arithmetic is not IFloatingPointBuiltins builtins)
            {
                throw new InvalidOperationException("数値組込み関数の演算部品が設定されていません。");
            }

            return builtins.EvaluateBuiltin(control, operation, left, right);
        }
    }

    private ushort RequireControlWord() => currentControl
        ?? throw new InvalidOperationException("この実行主体では浮動小数点制御を使用していません。");

    /// <summary>
    /// 自身を終了し、最後の主体なら演算部品を解放します。
    /// </summary>
    /// <remarks>rootの終了後は新しい主体を生成できません。</remarks>
    public void Dispose()
    {
        lock (domain.Gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (rootOwner)
            {
                domain.Closing = true;
            }

            if (--domain.Owners == 0)
            {
                domain.Arithmetic?.Dispose();
            }
        }
    }

    private sealed class Domain(Func<IFloatingPointArithmetic> load, bool startManaged)
    {
        internal readonly object Gate = new();
        internal readonly bool StartManaged = startManaged;
        internal IFloatingPointArithmetic? Arithmetic;
        internal ushort InitialControl;
        internal ushort? DefaultControl;
        internal int Owners = 1;
        internal bool Closing;

        internal void Initialize()
        {
            if (Arithmetic is not null)
            {
                return;
            }

            IFloatingPointArithmetic arithmetic = load();
            try
            {
                InitialControl = arithmetic.ReadControlWord();
                Arithmetic = arithmetic;
            }
            catch (Exception primary)
            {
                FloatingPointCleanup.Release(arithmetic, primary);
                throw;
            }
        }
    }
}
