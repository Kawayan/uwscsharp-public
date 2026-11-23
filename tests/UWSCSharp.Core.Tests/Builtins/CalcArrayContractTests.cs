using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class CalcArrayContractTests
{
    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, -3)]
    [InlineData(3, 7)]
    [InlineData(4, 2)]
    public void EveryModeSkipsOnlyEmptyAndConvertsFiniteNumericText(int mode, double expected)
    {
        var source = UwscArray.FromValues([Value.Numeric(-3), Value.Empty,
            Value.String("2"), Value.Numeric(7)]);
        Assert.Equal(Value.Numeric(expected), ArrayOperations.Calculate(source, mode));
        Assert.Equal(Value.Numeric(-3), source.Get(0));
        Assert.Equal(Value.String("2"), source.Get(2));
        Assert.Equal(mode == 1 ? Value.Numeric(0) : Value.Empty,
            ArrayOperations.Calculate(source, mode, 99));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EmptyInputsAndRangesHaveExplicitKinds(int mode)
    {
        Value expected = mode == 1 ? Value.Numeric(0) : Value.Empty;
        Assert.Equal(expected, ArrayOperations.Calculate(UwscArray.FromValues([]), mode));
        Assert.Equal(expected, ArrayOperations.Calculate(
            UwscArray.FromValues([Value.Empty, default]), mode));
        var array = UwscArray.FromValues([Value.Numeric(1)]);
        Assert.Equal(expected, ArrayOperations.Calculate(array, mode, 1, 0));
        Assert.Equal(expected, ArrayOperations.Calculate(array, mode, 0, -1));
    }

    [Fact]
    public void NumericTextUsesTheSharedExponentAndLengthRules()
    {
        string longToken = new string('0', 1000) + "1";
        var array = UwscArray.FromValues([Value.String(longToken), Value.String(" 1.5e2 ")]);
        Assert.Equal(Value.Numeric(151), ArrayOperations.Calculate(array, 1));
        Assert.Equal(Value.Numeric(75.5), ArrayOperations.Calculate(array, 4));
    }

    [Fact]
    public void InclusiveRangesFlattenAndRebaseThePositionWithoutChangingTheSource()
    {
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7],
            Enumerable.Range(1, 6).Select(n => Value.Numeric(n)));
        Assert.Equal(Value.Numeric(14), ArrayOperations.Calculate(source, 1, 1, 4));
        Assert.Equal(Value.Numeric(3.5), ArrayOperations.Calculate(source, 4, 1, 4));
        Assert.Equal(Value.Numeric(5), ArrayOperations.Calculate(source.Get(-1).AsArray(), 4));
        Assert.Equal([-2, 5], source.LowerBounds);
        Assert.Equal([-1, 7], source.UpperBounds);
    }

    [Theory]
    [InlineData("1.9", "2.9", 2)]
    [InlineData("-1e20", "1e20", 6)]
    [InlineData("1e20", "1e20", 0)]
    [InlineData("0", "-1e20", 0)]
    public void FiniteBoundsTruncateAndClampWithoutAnInt32Limit(string start, string end, double sum)
    {
        var source = Value.FromArray(UwscArray.FromValues(
            [Value.Numeric(1), Value.Numeric(2), Value.Empty, Value.Numeric(3)]));
        Assert.Equal(Value.Numeric(sum), Call(source, Value.Numeric(1),
            Value.String(start), Value.String(end)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.9)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(1e20)]
    public void InvalidFiniteModesAreRejectedEvenForEmptyArrays(double mode)
    {
        Assert.Throws<InvalidOperationException>(() => Call(
            Value.FromArray(UwscArray.FromValues([])), Value.Numeric(mode)));
    }

    [Theory]
    [InlineData("2.9", -3)]
    [InlineData("3.9", 7)]
    [InlineData("4.9", 2)]
    public void ModeConversionUsesFiniteTruncation(string mode, double expected)
    {
        var array = Value.FromArray(UwscArray.FromValues(
            [Value.Numeric(-3), Value.Numeric(2), Value.Numeric(7)]));
        Assert.Equal(Value.Numeric(expected), Call(array, Value.String(mode)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void UnsupportedOrNonfiniteElementsAreNotSilentlyIgnored(int mode)
    {
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()), new(ValueKind.Omitted),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var array = UwscArray.FromValues([value, Value.Numeric(2)]);
            Assert.Throws<InvalidOperationException>(() => ArrayOperations.Calculate(array, mode));
            Assert.Equal(Value.Numeric(2), ArrayOperations.Calculate(array, mode, 1));
            Assert.Equal(value, array.Get(0));
        }

        foreach (string text in new[] { "", "bad", "$10", "1,234", "NaN", "Infinity", "1e", "1\0", "１" })
        {
            Assert.Throws<InvalidOperationException>(() => ArrayOperations.Calculate(
                UwscArray.FromValues([Value.String(text)]), mode));
        }
    }

    [Fact]
    public void InvalidArgumentsAreRejectedBeforeConsideringAnEmptyRange()
    {
        var empty = Value.FromArray(UwscArray.FromValues([]));
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            for (int index = 1; index < 4; index++)
            {
                Value[] arguments = [empty, Value.Numeric(1), Value.Empty, Value.Numeric(int.MaxValue)];
                arguments[index] = value;
                Assert.Throws<InvalidOperationException>(() => Call(arguments));
            }

            if (value.Kind != ValueKind.Array)
            {
                Assert.Throws<InvalidOperationException>(() => Call(value, Value.Numeric(1)));
            }
        }

        foreach (string text in new[] { "$1", "NaN", "Infinity", "", "1e", "1\0" })
        {
            for (int index = 1; index < 4; index++)
            {
                Value[] arguments = [empty, Value.Numeric(1), Value.Empty, Value.Numeric(int.MaxValue)];
                arguments[index] = Value.String(text);
                Assert.Throws<InvalidOperationException>(() => Call(arguments));
            }
        }
    }

    [Fact]
    public void AdditionRejectsIntermediateOverflowEvenWhenLaterTermsWouldCancelIt()
    {
        double maximum = double.MaxValue;
        var array = UwscArray.FromValues([Value.Numeric(maximum), Value.Numeric(maximum),
            Value.Numeric(-maximum)]);
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Calculate(array, 1));
        Assert.Equal(Value.Numeric(-maximum), ArrayOperations.Calculate(array, 2));
        Assert.Equal(Value.Numeric(maximum), ArrayOperations.Calculate(array, 3));
        double average = ArrayOperations.Calculate(array, 4).Number;
        Assert.True(double.IsFinite(average));
        Assert.InRange(Math.Abs(average - maximum / 3), 0, maximum * 1e-15);
    }

    [Fact]
    public void AverageAvoidsOverflowInBothSumsAndDifferences()
    {
        double maximum = double.MaxValue;
        foreach (double number in new[] { maximum, -maximum, 1e308, -1e308 })
        {
            var repeated = UwscArray.FromValues(Enumerable.Repeat(Value.Numeric(number), 3));
            Assert.Equal(Value.Numeric(number), ArrayOperations.Calculate(repeated, 4));
            var opposite = UwscArray.FromValues([Value.Numeric(number), Value.Numeric(-number)]);
            Assert.Equal(0, ArrayOperations.Calculate(opposite, 4).Number);
        }

        var fractional = UwscArray.FromValues([Value.Numeric(1), Value.Numeric(2), Value.Numeric(4)]);
        Assert.InRange(Math.Abs(ArrayOperations.Calculate(fractional, 4).Number - 7d / 3), 0, 1e-15);
    }

    [Fact]
    public void ASelectedSingleNegativeZeroRetainsItsSignForMinMaxAndAverage()
    {
        var source = UwscArray.FromValues([Value.Empty, Value.Numeric(-0d)]);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(ArrayOperations.Calculate(source, 1).Number));
        foreach (int mode in new[] { 2, 3, 4 })
        {
            Assert.Equal(long.MinValue,
                BitConverter.DoubleToInt64Bits(ArrayOperations.Calculate(source, mode).Number));
        }
    }

    [Fact]
    public void MaximumArraySizeUsesConstantAuxiliarySpace()
    {
        var source = UwscArray.Create([UwscArray.MaximumElements - 1],
            Enumerable.Repeat(Value.Numeric(1), UwscArray.MaximumElements));
        Assert.Equal(Value.Numeric(1), ArrayOperations.Calculate(source, 4, 0, 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(Value.Numeric(UwscArray.MaximumElements), ArrayOperations.Calculate(source, 1));
        Assert.Equal(Value.Numeric(1), ArrayOperations.Calculate(source, 2));
        Assert.Equal(Value.Numeric(1), ArrayOperations.Calculate(source, 3));
        Assert.Equal(Value.Numeric(1), ArrayOperations.Calculate(source, 4));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 65536);
        Assert.Equal(Value.Numeric(1), source.Get(UwscArray.MaximumElements - 1));
    }

    [Fact]
    public async Task ConstantsDefaultsOmissionsAndSameStringReachTheKernel()
    {
        Assert.Equal(["6", "1", "3", "2", "1", "6", "0"], await Run("""
            DIM a[]=1,2,EMPTY,3
            PRINT CALCARRAY(a,CALC_ADD)
            PRINT CALCARRAY(a,CALC_MIN)
            PRINT CALCARRAY(a,CALC_MAX)
            PRINT CALCARRAY(a,CALC_AVR)
            PRINT CALCARRAY(a,1,EMPTY,EMPTY)
            PRINT CALCARRAY(a,1,,)
            PRINT cAlCaRrAy(a,1,1e20)
            """));
        Assert.Equal(["6"], await Run(
            "OPTION SAMESTR\nDIM a[]=1,2,3\nPRINT CALCARRAY(a,1)"));
    }

    [Theory]
    [InlineData("CALCARRAY()")]
    [InlineData("CALCARRAY(a,)")]
    [InlineData("CALCARRAY(a,Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nDIM a[]=1\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceInOrderAndFailureKeepsTheDestination()
    {
        Assert.Equal(["array", "mode", "start", "end", "2", "kept"], await Run("""
            result=CALCARRAY(Make(),Tick("mode"),Tick("start"),Tick("end"))
            PRINT result
            result="kept"
            TRY
                DIM a[]=1,"bad"
                result=CALCARRAY(a,1)
            EXCEPT
                PRINT result
            ENDTRY
            FUNCTION Make()
                PRINT "array"
                DIM values[]=1,2,3
                RESULT=values
            FEND
            FUNCTION Tick(name)
                PRINT name
                RESULT=1
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("DIM a[]=1\nPRINT CALCARRAY(a,NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT CALCARRAY(Tick(),1)\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=1\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Call(params Value[] arguments) =>
        BuiltinFunctions.Evaluate("CALCARRAY", arguments);
}
