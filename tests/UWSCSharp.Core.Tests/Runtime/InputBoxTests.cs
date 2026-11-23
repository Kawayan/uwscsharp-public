using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputBoxTests
{
    [Fact]
    public void DefaultsAndOmittedArgumentsDoNotBecomeNumericZero()
    {
        var request = InputBoxRequest.Read([Value.String("確認")]);
        Assert.Equal("", request.DefaultText);
        Assert.False(request.Password);
        Assert.Null(request.X);
        Assert.Null(request.Y);
        request = InputBoxRequest.Read([Value.String("確認"), new(ValueKind.Omitted),
            Value.Numeric(1), Value.Numeric(-1), Value.Numeric(-1)]);
        Assert.True(request.Password);
        Assert.Equal(-1, request.X);
        Assert.Equal(-1, request.Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(42)]
    public void NumericDefaultsUseTheSharedTextConversion(double number)
    {
        var request = InputBoxRequest.Read([Value.String("確認"), Value.Numeric(number)]);
        Assert.Equal(Value.Numeric(number).Display(), request.DefaultText);
    }

    [Fact]
    public void RejectsInvalidValuesBeforeCreatingNativeControls()
    {
        Assert.Throws<InvalidOperationException>(() => InputBoxRequest.Read([]));
        Assert.Throws<InvalidOperationException>(() => InputBoxRequest.Read([new(ValueKind.Omitted)]));
        Assert.Throws<InvalidOperationException>(() => InputBoxRequest.Read(
            [Value.String("確認"), Value.String("a\0b")]));
        Assert.Throws<InvalidOperationException>(() => InputBoxRequest.Read(
            [Value.String("確認"), Value.String(""), Value.Numeric(double.NaN)]));
        Assert.Throws<InvalidOperationException>(() => InputBoxRequest.Read(
            [Value.String("確認"), Value.String(""), Value.Numeric(0), Value.Numeric(double.PositiveInfinity)]));
    }

    [Fact]
    public void CoordinatesTruncateAndAnIncompletePairSelectsTheCenter()
    {
        var request = InputBoxRequest.Read([Value.String("確認"), new(ValueKind.Omitted),
            Value.Numeric(0), Value.Numeric(12.9), Value.Numeric(-4.8)]);
        Assert.Equal(12, request.X);
        Assert.Equal(-4, request.Y);
        request = InputBoxRequest.Read([Value.String("確認"), new(ValueKind.Omitted),
            Value.Numeric(0), Value.Numeric(12)]);
        Assert.Null(request.X);
        Assert.Null(request.Y);
    }

    [Theory]
    [InlineData("INPUT()")]
    [InlineData("INPUT(,)")]
    [InlineData("INPUT(Probe(),1,2,3,4,Probe())")]
    public async Task InvalidArityHasNoArgumentEffects(string expression)
    {
        List<string> output = [];
        await new Interpreter(windows: new Adapter()).RunAsync($$"""
            TRY
                {{expression}}
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION Probe()
                PRINT "side"
                RESULT="value"
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
    }

    [Fact]
    public async Task InterpreterPassesOmittedFieldsAndPreservesNumericLookingText()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            "PRINT INPUT(\"確認\",\"001.50\",,80,90)", output.Add);
        Assert.Equal(["001.50"], output);
        Assert.Equal(80, adapter.Request!.X);
        Assert.Equal(90, adapter.Request.Y);
        Assert.False(adapter.Request.Password);
    }

    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal InputBoxRequest? Request;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> values,
            CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.Input, name);
            Request = InputBoxRequest.Read(values);
            return ValueTask.FromResult(Value.String(Request.DefaultText));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
