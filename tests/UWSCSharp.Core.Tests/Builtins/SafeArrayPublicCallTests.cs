using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// Native生成を使わず、公開名の解決と評価前検査を確認します。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayPublicCallTests
{
    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Sessions;
        internal List<Value[]> Calls { get; } = [];

        public IWindowsAutomationSession CreateSession()
        {
            Sessions++;
            return this;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.SafeArray, name);
            Calls.Add([.. arguments]);
            var request = SafeArrayRequest.Read(arguments);
            return ValueTask.FromResult(Value.FromArray(UwscArray.CreateBounded(
                request.LowerBounds, request.UpperBounds)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("SAFEARRAY()")]
    [InlineData("sAfEaRrAy(1)")]
    [InlineData("SAFEARRAY(0,1)")]
    [InlineData("SAFEARRAY(0,1,3)")]
    [InlineData("SAFEARRAY(-2,-1,5,7)")]
    [InlineData("SAFEARRAY(-2,-1,5,7,8)")]
    [InlineData("SAFEARRAY(0,,3)")]
    [InlineData("SAFEARRAY(0,1,)")]
    [InlineData("SAFEARRAY(0,1,5,7,)")]
    public async Task PublicFormsReachTheWindowsAdapter(string call)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(call, _ => { });
        Assert.Single(automation.Calls);
        Assert.True(BuiltinFunctions.IsBuiltin(BuiltinFunctionNames.SafeArray));
    }

    [Theory]
    [InlineData("SAFEARRAY(ArrayArgumentEffect(),1,2,3,4,5)")]
    [InlineData("SAFEARRAY(,ArrayArgumentEffect())")]
    [InlineData("SAFEARRAY(ArrayArgumentEffect(),1,,2)")]
    [InlineData("SAFEARRAY(ArrayArgumentEffect(),1,2,)")]
    public async Task InvalidSyntaxRejectsBeforeAnyArgumentSideEffect(string call)
    {
        var automation = new Automation();
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(call + """

                FUNCTION ArrayArgumentEffect()
                    PRINT "evaluated"
                    RESULT=0
                FEND
                """, output.Add));
        Assert.Contains("引数の数または省略位置が不正", error.Message);
        Assert.Contains(BuiltinFunctionNames.SafeArray, error.Message);
        Assert.Empty(output);
        Assert.Empty(automation.Calls);
    }

    [Fact]
    public async Task ValidArgumentsAreEvaluatedOnceFromLeftToRight()
    {
        var automation = new Automation();
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync("""
            SAFEARRAY(Echo(0),Echo(1),Echo(2),Echo(3),Echo(4))
            FUNCTION Echo(x)
                PRINT x
                RESULT=x
            FEND
            """, output.Add);
        Assert.Equal(["0", "1", "2", "3", "4"], output);
        Assert.Equal(Enumerable.Range(0, 5).Select(x => Value.Numeric(x)),
            Assert.Single(automation.Calls));
    }

    [Theory]
    [InlineData("SAFEARRAY(0,1000000)")]
    [InlineData("SAFEARRAY(0,999,0,1000)")]
    [InlineData("SAFEARRAY(EMPTY)")]
    [InlineData("SAFEARRAY(0,1,1)")]
    public async Task InvalidValuesAreRejectedBeforeTheAdapter(string call)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(call, _ => { }));
        Assert.Empty(automation.Calls);
    }

}
