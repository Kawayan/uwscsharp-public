using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の演算部品でbridgeだけを確認し、物理CWとGUIを変更しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class FloatingPointArithmeticBridgeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void BridgeMapsTheOperatorAndPreservesTheOwnedControlWord(int source, int target)
    {
        var api = new Arithmetic { Result = new(0, 12, 0) };
        using var owner = new FloatingPointEnvironment(api);
        owner.SetControlWord(0x87f);
        var bridge = new FloatingPointArithmeticBridge(owner);
        Assert.Equal(12, bridge.Evaluate((NumericBinaryOperation)source, 17, 5));
        Assert.Equal((FloatingPointOperation)target, api.Operation);
        Assert.Equal((ushort)0x87f, api.Control);
        Assert.Equal((17d, 5d), api.Inputs);
    }

    [Fact]
    public void BridgeKeepsTheNativeCodeAndFaultWordInTheFailure()
    {
        var api = new Arithmetic { Result = new(0xc0000091, 0, 0xb8a80277) };
        using var owner = new FloatingPointEnvironment(api);
        var bridge = new FloatingPointArithmeticBridge(owner);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            bridge.Evaluate(NumericBinaryOperation.Multiply, 17, 5));
        Assert.Contains("0xc0000091", failure.Message);
        Assert.Equal(0xc0000091u, failure.Data["UWSCSharp.FloatingPointCode"]);
        Assert.Equal(0xb8a80277u, failure.Data["UWSCSharp.FloatingPointState"]);
        Assert.Equal((ushort)0x27f, owner.CurrentControlWord);
    }

    [Fact]
    public void UnsupportedOperatorNeverCallsTheNativeBackend()
    {
        var api = new Arithmetic();
        using var owner = new FloatingPointEnvironment(api);
        var bridge = new FloatingPointArithmeticBridge(owner);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            bridge.Evaluate((NumericBinaryOperation)99, 17, 5));
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public void ThreadScopesKeepTheirCurrentWordAndOwnOnlyTheirFork()
    {
        var api = new Arithmetic();
        using var owner = new FloatingPointEnvironment(api);
        var bridge = new FloatingPointArithmeticBridge(owner);
        owner.SetControlWord(0x87f);
        using INumericArithmeticScope first = bridge.ForkForThread();
        owner.SetControlWord(0x47f);
        _ = first.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x87f, api.Control);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            first.Evaluate(NumericBinaryOperation.Add, 1, 2));
        _ = bridge.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x47f, api.Control);
        using INumericArithmeticScope second = bridge.ForkForThread();
        owner.Dispose();
        Assert.Equal(0, api.Disposals);
        Assert.Throws<ObjectDisposedException>(() => bridge.ForkForThread());
        _ = second.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x47f, api.Control);
        second.Dispose();
        Assert.Equal(1, api.Disposals);
    }

    [Fact]
    public void ControlInterfaceChangesTheCurrentSubjectAndFutureForkDefault()
    {
        var api = new Arithmetic();
        using var owner = new FloatingPointEnvironment(api);
        var bridge = new FloatingPointArithmeticBridge(owner);
        bridge.SetControlWord(0x87f);
        using INumericArithmeticScope first = bridge.ForkForThread();
        INumericControlArithmetic control =
            Assert.IsAssignableFrom<INumericControlArithmetic>(first);
        control.SetControlWord(0x47f);
        _ = first.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x47f, api.Control);
        _ = bridge.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x87f, api.Control);
        using INumericArithmeticScope second = bridge.ForkForThread();
        _ = second.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x47f, api.Control);
        control.SetControlWord(0);
        _ = first.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x27f, api.Control);
        _ = second.Evaluate(NumericBinaryOperation.Add, 1, 2);
        Assert.Equal((ushort)0x47f, api.Control);
    }

    public static TheoryData<int> BuiltinOperations => new(
        Enum.GetValues<NumericBuiltinOperation>().Select(operation => (int)operation));

    [Theory]
    [MemberData(nameof(BuiltinOperations))]
    public void BuiltinMappingUsesTheCurrentSubjectAndCopiesTheNativeFailure(int operation)
    {
        var api = new Arithmetic { Result = new(0xc0000091, 0, 0xb8a80277) };
        using var owner = new FloatingPointEnvironment(api);
        var bridge = new FloatingPointArithmeticBridge(owner);
        bridge.SetControlWord(0x87f);
        using INumericArithmeticScope scope = bridge.ForkForThread();
        INumericControlArithmetic control = Assert.IsAssignableFrom<INumericControlArithmetic>(scope);
        control.SetControlWord(0x47f);
        INumericBuiltinArithmetic builtins = Assert.IsAssignableFrom<INumericBuiltinArithmetic>(scope);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            builtins.EvaluateBuiltin((NumericBuiltinOperation)operation, 17, 5));
        var expected = (FloatingPointBuiltinOperation)operation;
        Assert.True(Enum.IsDefined(expected));
        Assert.Equal(expected, api.BuiltinOperation);
        Assert.Equal((ushort)0x47f, api.Control);
        Assert.Equal((17d, 5d), api.Inputs);
        Assert.Equal(0xc0000091u, failure.Data["UWSCSharp.FloatingPointCode"]);
        Assert.Equal(0xb8a80277u, failure.Data["UWSCSharp.FloatingPointState"]);
        Assert.Equal((ushort)0x87f, owner.CurrentControlWord);
    }

    [Fact]
    public async Task DeferredOwnerUsesManagedArithmeticBeforeActivationAndAfterReset()
    {
        var api = new Arithmetic { Result = new(0, 42, 0) };
        int loads = 0;
        using var owner = new FloatingPointEnvironment(() =>
        {
            loads++;
            return api;
        });
        var output = new List<string>();
        var options = new ScriptRunOptions([])
        {
            NumericArithmetic = new FloatingPointArithmeticBridge(owner)
        };

        await new Interpreter().RunAsync("""
            SET8087CW(0)
            PRINT 2 + 3
            PRINT SQRT(4)
            """, output.Add, default, options);
        Assert.Equal(0, loads);

        await new Interpreter().RunAsync("""
            SET8087CW(2175)
            PRINT 2 + 3
            PRINT SQRT(4)
            SET8087CW(0)
            PRINT 2 + 3
            """, output.Add, default, options);
        Assert.Equal(["5", "2", "42", "42", "5"], output);
        Assert.Equal(1, loads);
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task ScriptCanRetryActivationAfterCaughtLoadFailure()
    {
        var api = new Arithmetic { Result = new(0, 42, 0) };
        int attempts = 0;
        using var owner = new FloatingPointEnvironment(() =>
        {
            if (++attempts == 1)
            {
                throw new DllNotFoundException("テスト用DLLがありません。");
            }

            return api;
        });
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            TRY
                SET8087CW(2175)
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            PRINT 2 + 3
            SET8087CW(1151)
            PRINT 2 + 3
            SET8087CW(0)
            PRINT 2 + 3
            """, output.Add, default, new ScriptRunOptions([])
        {
            NumericArithmetic = new FloatingPointArithmeticBridge(owner)
        });
        Assert.Equal(["2", "5", "42", "5"], output);
        Assert.Equal(2, attempts);
        Assert.Equal(1, api.Calls);
        Assert.Equal((ushort)0x47f, api.Control);
        Assert.False(owner.IsActive);
    }

    [Fact]
    public async Task OrdinaryWindowsInterpreterCanResetWithoutLoadingTheDll()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            PRINT "<"+SET8087CW()+">"
            PRINT 2+3
            """, output.Add);

        Assert.Equal(["<>", "5"], output);
    }

    [Fact]
    public async Task NativeDllIntegerArgumentsUseTheActiveRoundingControl()
    {
        using var owner = new FloatingPointEnvironment(new Arithmetic());
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            DEF_DLL MulDiv(int,int,int):long:kernel32.dll
            SET8087CW($077f)
            PRINT MulDiv(-2.5,1,1)
            SET8087CW($0b7f)
            PRINT MulDiv(-2.5,1,1)
            SET8087CW(0)
            PRINT MulDiv(-2.5,1,1)
            """, output.Add, default, new ScriptRunOptions([])
        {
            NumericArithmetic = new FloatingPointArithmeticBridge(owner)
        });

        Assert.Equal(["-3", "-2", "-2"], output);
    }

    private sealed class Arithmetic : IFloatingPointBuiltins
    {
        internal FloatingPointResult Result;
        internal FloatingPointOperation Operation;
        internal FloatingPointBuiltinOperation BuiltinOperation;
        internal ushort Control;
        internal (double, double) Inputs;
        internal int Calls;
        internal int Disposals;

        public ushort ReadControlWord() => 0x27f;

        public FloatingPointResult Evaluate(ushort control,
            FloatingPointOperation operation, double left, double right)
        {
            Calls++;
            Operation = operation;
            Control = control;
            Inputs = (left, right);
            return Result;
        }

        public void Dispose()
        {
            Disposals++;
        }

        public FloatingPointResult EvaluateBuiltin(ushort control,
            FloatingPointBuiltinOperation operation, double left, double right)
        {
            Control = control;
            BuiltinOperation = operation;
            Inputs = (left, right);
            Calls++;
            return Result;
        }
    }
}
