using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputLockContractTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(0.5, false)]
    [InlineData(-1.5, true)]
    public void FlagUsesExistingNumericConversion(double number, bool expected)
    {
        Assert.Equal(expected, InputLockRequest.Read([Value.Numeric(number)]).Blocked);
        Assert.Equal(expected, InputLockRequest.Read([Value.String(
            number.ToString(System.Globalization.CultureInfo.InvariantCulture))]).Blocked);
    }

    [Fact]
    public void OmissionAndEmptyReleaseInput()
    {
        Assert.False(InputLockRequest.Read([]).Blocked);
        Assert.False(InputLockRequest.Read([Value.Empty]).Blocked);
        Assert.False(InputLockRequest.Read([new Value(ValueKind.Omitted)]).Blocked);
    }

    [Fact]
    public void InvalidValuesAreRejectedBeforeTheAdapter()
    {
        foreach (Value value in new[] { Value.String("invalid"), new Value(ValueKind.Null),
            new Value(ValueKind.Number, double.NaN), new Value(ValueKind.Array) })
        {
            Assert.Throws<InvalidOperationException>(() =>
                WindowsFunctions.Validate(BuiltinFunctionNames.LockHard, [value]));
        }
    }

    [Fact]
    public async Task InvalidArityDoesNotEvaluateArguments()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC calls=0
            TRY
                LOCKHARD(Tick(),Tick())
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick()
                calls=calls+1
                RESULT=1
            FEND
            """, output.Add);
        Assert.Equal(["0"], output);
    }
}
