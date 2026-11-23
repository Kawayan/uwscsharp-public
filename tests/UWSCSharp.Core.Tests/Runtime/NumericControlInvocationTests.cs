using Xunit;

namespace UWSCSharp.Core.Tests;

// 内部指定の偽演算部品だけを使い、物理CW・GUI・入力を変更しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NumericControlInvocationTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("EMPTY", 0)]
    [InlineData("-0.9", 0)]
    [InlineData("65535.9", 65535)]
    [InlineData("$087f", 2175)]
    [InlineData("\"$027f\"", 639)]
    [InlineData("\" 2.9 \"", 2)]
    [InlineData("TRUE", 1)]
    public async Task BuiltinConvertsOneArgumentAndReturnsAnEmptyString(string argument, int expected)
    {
        var control = new Arithmetic();
        var output = new List<string>();
        await Run("PRINT \"<\"+sEt8087Cw(" + argument + ")+\">\"", control, output);
        Assert.Equal(["<>"], output);
        Assert.Equal([(ushort)expected], control.Words);
        Assert.Equal(0, control.Disposals);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("4294967296")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"bad\"")]
    [InlineData("\"\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e999\"")]
    [InlineData("SPLIT(\"a\",\",\")")]
    public async Task InvalidValuesDoNotChangeControl(string argument)
    {
        var control = new Arithmetic();
        var output = new List<string>();
        await Run("TRY\nSET8087CW(" + argument + ")\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY",
            control, output);
        Assert.Equal(["2"], output);
        Assert.Empty(control.Words);
    }

    [Fact]
    public async Task ExcessArgumentsAreRejectedBeforeEitherSideEffect()
    {
        var control = new Arithmetic();
        var output = new List<string>();
        await Run("""
            PUBLIC count=0
            TRY
                SET8087CW(MakeControl(),MakeControl())
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            PRINT count
            FUNCTION MakeControl()
                count=count+1
                RESULT=1
            FEND
            """, control, output);
        Assert.Equal(["3", "0"], output);
        Assert.Empty(control.Words);
    }

    [Fact]
    public async Task OneArgumentIsEvaluatedOnceBeforeSettingControl()
    {
        var control = new Arithmetic();
        var output = new List<string>();
        await Run("""
            PUBLIC count=0
            SET8087CW(MakeControl())
            PRINT count
            FUNCTION MakeControl()
                count=count+1
                RESULT=$027f
            FEND
            """, control, output);
        Assert.Equal(["1"], output);
        Assert.Equal([(ushort)0x27f], control.Words);
    }

    [Fact]
    public async Task CancellationDuringArgumentEvaluationDoesNotSetControl()
    {
        using var cancellation = new CancellationTokenSource();
        var control = new Arithmetic { Calculating = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("SET8087CW(1+1)", _ => { }, cancellation.Token,
                new ScriptRunOptions([]) { NumericArithmetic = control }));
        Assert.Empty(control.Words);
        Assert.Equal(0, control.Disposals);
    }

    [Fact]
    public async Task SetterFailureKeepsItsCallLineAndOriginalCause()
    {
        var expected = new InvalidOperationException("CW変更の失敗");
        var control = new Arithmetic { SetFailure = expected };
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nSET8087CW(639)", control, []));
        Assert.Equal(2, failure.Line);
        Assert.Same(expected, failure.InnerException);
        Assert.Equal(0, control.Disposals);
    }

    [Fact]
    public async Task EvalUsesTheCurrentControlCapability()
    {
        var control = new Arithmetic();
        var output = new List<string>();
        await Run("PRINT \"a\"+EVAL(\"SET8087CW(639)\")+\"b\"", control, output);
        Assert.Equal(["ab"], output);
        Assert.Equal([(ushort)639], control.Words);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWithoutControlReportsAnUnsupportedEnvironment(
        bool arithmeticOnly)
    {
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("SET8087CW(0)", _ => { }, default,
                new ScriptRunOptions([])
                {
                    NumericArithmetic = arithmeticOnly ? new PlainArithmetic() : null
                }));

        Assert.Equal(1, failure.Line);
        Assert.Contains("使用できません", failure.Message);
    }

    [Fact]
    public void BuiltinNameIsReservedForRoutineDeclarations()
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile("""
            FUNCTION SET8087CW(value)
                RESULT=value+1
            FEND
            """));
    }

    [Fact]
    public async Task EachRunOwnsAndReleasesItsScopeEvenWhenTheScriptFails()
    {
        var automation = new Automation();
        var interpreter = new Interpreter(windows: automation);
        await interpreter.RunAsync("SET8087CW(639)", _ => { });
        await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync("SET8087CW(65536)", _ => { }));

        Assert.Equal(2, automation.Scopes.Count);
        Assert.Equal([(ushort)639], automation.Scopes[0].Words);
        Assert.Empty(automation.Scopes[1].Words);
        Assert.All(automation.Scopes, scope => Assert.Equal(1, scope.Disposals));

        var borrowed = new Arithmetic();
        await interpreter.RunAsync("SET8087CW(0)", _ => { }, default,
            new ScriptRunOptions([]) { NumericArithmetic = borrowed });

        Assert.Equal(2, automation.Scopes.Count);
        Assert.Equal([(ushort)0], borrowed.Words);
        Assert.Equal(0, borrowed.Disposals);
    }

    private static Task Run(string source, Arithmetic control, List<string> output) =>
        new Interpreter().RunAsync(source, output.Add, default,
            new ScriptRunOptions([]) { NumericArithmetic = control });

    private sealed class Arithmetic : INumericControlArithmetic,
        INumericArithmeticScope
    {
        internal List<ushort> Words { get; } = [];
        internal Exception? SetFailure;
        internal Action? Calculating;
        internal int Disposals;

        public void SetControlWord(ushort word)
        {
            if (SetFailure is not null)
            {
                throw SetFailure;
            }

            Words.Add(word);
        }

        public double Evaluate(NumericBinaryOperation operation, double left, double right)
        {
            Calculating?.Invoke();
            return left + right;
        }

        public void Dispose() => Disposals++;
    }

    private sealed class Automation : IWindowsAutomation,
        IWindowsAutomationSession, INumericArithmeticProvider
    {
        internal List<Arithmetic> Scopes { get; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public INumericArithmeticScope CreateNumericArithmetic()
        {
            var scope = new Arithmetic();
            Scopes.Add(scope);
            return scope;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PlainArithmetic : INumericArithmetic
    {
        public double Evaluate(NumericBinaryOperation operation, double left, double right) =>
            left + right;
    }
}
