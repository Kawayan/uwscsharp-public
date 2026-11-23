using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ReplaceContractTests
{
    [Theory]
    [InlineData("REPLACE")]
    [InlineData("rEpLaCe")]
    public void PureContextResolvesAliasAndKeepsItsTypeContract(string name)
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        var variables = new Dictionary<string, Value>
        {
            ["text"] = Value.String("\ud800\udc00\udc00"),
            ["from"] = Value.String("\udc00"),
            ["to"] = Value.String("x")
        };
        Assert.Equal("\ud800\udc00x", ExpressionEvaluator.Evaluate(
            name + "(text,from,to)", variables).Text);
        variables["text"] = Value.Numeric(double.MaxValue);
        variables["from"] = Value.String("E+308");
        variables["to"] = Value.String("z");
        Assert.Equal("1.7976931348623157z", ExpressionEvaluator.Evaluate(
            name + "(text,from,to)", variables).Text);
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromArray(array), Value.FromHashTable(table), Value.FromObject(new object()),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity)];
        foreach (Value value in invalid)
        {
            foreach (string argument in new[] { "text", "from", "to" })
            {
                variables["text"] = Value.String("");
                variables["from"] = Value.String("");
                variables["to"] = Value.String("");
                variables[argument] = value;
                Assert.Throws<InvalidOperationException>(() =>
                    ExpressionEvaluator.Evaluate(name + "(text,from,to)", variables));
            }
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("", "xx")]
    [InlineData("OPTION SAMESTR\n", "Ax")]
    public async Task CallsInsideFunctionsUseTheSameProgramSetting(string option, string expected)
    {
        Assert.Equal([expected, expected], await Run(option + """
            PRINT ThroughReplace("Aa")
            PRINT ThroughChgMoj("Aa")
            FUNCTION ThroughReplace(text)
                RESULT=REPLACE(text,"a","x")
            FEND
            FUNCTION ThroughChgMoj(text)
                RESULT=CHGMOJ(text,"a","x")
            FEND
            """));
    }

    [Theory]
    [InlineData("REPLACE()")]
    [InlineData("REPLACE(Tick())")]
    [InlineData("REPLACE(Tick(),Tick())")]
    [InlineData("REPLACE(,Tick(),Tick())")]
    [InlineData("REPLACE(Tick(),,Tick())")]
    [InlineData("REPLACE(Tick(),Tick(),)")]
    [InlineData("REPLACE(Tick(),Tick(),Tick(),Tick())")]
    public async Task AliasArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }
}
