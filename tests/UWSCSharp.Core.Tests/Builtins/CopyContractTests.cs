using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class CopyContractTests
{
    [Theory]
    [InlineData("COPY(\"ABCDE\",2,3)", "BCD")]
    [InlineData("COPY(\"abc\",2)", "bc")]
    [InlineData("COPY(\"abc\",2,)", "bc")]
    [InlineData("COPY(\"abc\",2,EMPTY)", "bc")]
    [InlineData("COPY(\"abc\",0,2)", "ab")]
    [InlineData("COPY(\"abc\",-2,2)", "ab")]
    [InlineData("COPY(\"abc\",1,0)", "")]
    [InlineData("COPY(\"abc\",1,-1)", "")]
    [InlineData("COPY(\"abc\",1,-0.5)", "")]
    [InlineData("COPY(\"abc\",1.9,1.9)", "a")]
    [InlineData("COPY(\"abc\",1e+308)", "")]
    [InlineData("COPY(\"abc\",-1e+308,2)", "ab")]
    [InlineData("COPY(\"abc\",1,1e+308)", "abc")]
    [InlineData("COPY(\"abc\",1,-1e+308)", "")]
    [InlineData("COPY(\"abc\",2147483647,2147483647)", "")]
    [InlineData("COPY(\"abc\",1,2147483648)", "abc")]
    [InlineData("COPY(\"abc\",\" 2e0 \",\"1.9\")", "b")]
    [InlineData("COPY(\"abc\",EMPTY)", "abc")]
    [InlineData("COPY(EMPTY,1)", "")]
    [InlineData("COPY(12345,2,2)", "23")]
    [InlineData("COPY(TRUE,1)", "1")]
    [InlineData("COPY(\"A😀BC\",2,2)", "😀B")]
    [InlineData("cOpY(\"abc\",2,1)", "b")]
    public void FiniteIndicesUseStringBoundaries(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LogicalSlicesPreserveEveryUtf16Unit(int scenario)
    {
        (string first, string middle, string last) = scenario switch
        {
            0 => ("\udc00", "x", "😀"),
            1 => ("a", "\ud800", "b"),
            2 => ("\udc00", "\udc01", "\ud800"),
            3 => ("a", "\0", "b"),
            _ => ("a", "e\u0301", "b")
        };
        string text = first + middle + last;
        int middleLength = middle == "e\u0301" ? 2 : 1;
        Assert.Equal(first, StringOperations.Copy(text, 1, 1));
        Assert.Equal(middle, StringOperations.Copy(text, 2, middleLength));
        Assert.Equal(last, StringOperations.Copy(text, 2 + middleLength, 1));
        Assert.Equal(text, StringOperations.Copy(text, 1));
        Assert.Equal("", StringOperations.Copy(text, 1, 0));
    }

    [Fact]
    public void FullFiniteRangeAndSameStringOptionKeepBoundaryRules()
    {
        Value[] all = [Value.String("Ab😀c"),
            Value.Numeric(double.MinValue), Value.Numeric(double.MaxValue)];
        Assert.Equal("Ab😀c", BuiltinFunctions.Evaluate("COPY", all).Text);
        Assert.Equal("Ab😀c", BuiltinFunctions.Evaluate("COPY", all,
            sameString: true).Text);
        Assert.Equal("", Copy(Value.String("abc"),
            Value.Numeric(double.MaxValue), Value.Numeric(1)));
        Assert.Equal("", Copy(Value.String("abc"),
            Value.Numeric(1), Value.Numeric(double.MinValue)));
    }

    [Fact]
    public void LongNumericTextAndLargeInputDoNotUseIndependentLimits()
    {
        string position = new('0', 10000);
        Assert.Equal("b", Copy(Value.String("abc"),
            Value.String(position + "2"), Value.Numeric(1)));
        string text = new('x', 1_000_001);
        Assert.Equal(text, Copy(Value.String(text),
            Value.Numeric(1), Value.Numeric(double.MaxValue)));
    }

    [Theory]
    [InlineData("COPY(\"abc\",\"$2\")")]
    [InlineData("COPY(\"abc\",\"1e+\")")]
    [InlineData("COPY(\"abc\",\"1,0\")")]
    [InlineData("COPY(\"abc\",\"1\"+CHR(0))")]
    [InlineData("COPY(\"abc\",1,\"NaN\")")]
    [InlineData("COPY(NULL,1)")]
    [InlineData("COPY(\"abc\",NULL)")]
    [InlineData("COPY(\"abc\",1,NULL)")]
    public async Task InvalidTypesAndNumericGrammarAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void InvalidKindsAndNonfiniteValuesNeverMutateInput()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [Value.Nothing, Value.FromError(5),
            Value.FromArray(array), Value.FromHashTable(table),
            Value.FromObject(new object()), new(ValueKind.Null),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Copy(value, Value.Numeric(1), Value.Numeric(1)));
            Assert.Throws<InvalidOperationException>(() =>
                Copy(Value.String("a"), value, Value.Numeric(1)));
            Assert.Throws<InvalidOperationException>(() =>
                Copy(Value.String("a"), Value.Numeric(1), value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("COPY()")]
    [InlineData("COPY(Tick())")]
    [InlineData("COPY(,Tick())")]
    [InlineData("COPY(Tick(),)")]
    [InlineData("COPY(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunInOrderOnce()
    {
        Assert.Equal(["1", "2", "3", "bcd", "3"], await Run(
            "PUBLIC calls=0\nPRINT COPY(Tick(),Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\n"
            + "IF calls=1 THEN RESULT=\"abcd\" ELSE RESULT=calls\nFEND"));
    }

    [Fact]
    public async Task InvalidIndexReportsLineAndCancellationSkipsEvaluation()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT COPY(\"a\",NULL)"));
        Assert.Equal(2, error.Line);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT COPY(Tick(),1)\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Copy(Value text, Value start, Value count) =>
        BuiltinFunctions.Evaluate("COPY", [text, start, count]).Text;
}
