using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PrintObjectTests
{
    [Fact]
    public async Task ReferencesUseIdentityWithoutCallingObjectMembers()
    {
        var first = new UncallableObject();
        var second = new UncallableObject();
        var windows = new Values(Value.FromObject(first), Value.FromObject(second));
        var output = new List<string>();
        await new Interpreter(windows: windows).RunAsync("""
            DIM a=GETSTR(1)
            DIM alias=a
            DIM b=GETSTR(2)
            PRINT a
            PRINT b
            PRINT alias
            PRINT NOTHING
            """, output.Add);
        Assert.Equal(["OBJECT(1)", "OBJECT(2)", "OBJECT(1)", "NOTHING"], output);
    }

    [Fact]
    public async Task SameInterpreterStartsNewNumberingForEachExecution()
    {
        var windows = new Values(Value.FromObject(new object()), Value.FromObject(new object()));
        var interpreter = new Interpreter(windows: windows);
        var output = new List<string>();
        await interpreter.RunAsync("PRINT GETSTR(1)", output.Add);
        await interpreter.RunAsync("PRINT GETSTR(2)\nPRINT GETSTR(1)", output.Add);
        Assert.Equal(["OBJECT(1)", "OBJECT(1)", "OBJECT(2)"], output);
    }

    [Fact]
    public async Task ArrayCopyAndFunctionKeepSameReferenceNumber()
    {
        var windows = new Values(Value.FromObject(new object()));
        var output = new List<string>();
        await new Interpreter(windows: windows).RunAsync("""
            DIM a=GETSTR(1)
            DIM values[]=a
            PRINT a
            PRINT values[0]
            RenderReference(a)
            PROCEDURE RenderReference(value)
                PRINT value
            FEND
            """, output.Add);
        Assert.Equal(["OBJECT(1)", "OBJECT(1)", "OBJECT(1)"], output);
    }

    [Fact]
    public async Task ObjectFormattingDoesNotEnableImplicitStringCoercion()
    {
        var windows = new Values(Value.FromObject(new object()));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: windows).RunAsync("DIM a=GETSTR(1)\nPRINT \"value:\"+a", _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task FormatterDoesNotEnumerateCollections()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            DIM values[]=1,2
            TRY
                PRINT values
            EXCEPT
                PRINT "array rejected"
            ENDTRY
            HASHTBL table
            TRY
                PRINT table
            EXCEPT
                PRINT "hash rejected"
            ENDTRY
            """, output.Add);
        Assert.Equal(["array rejected", "hash rejected"], output);
    }

    private sealed class UncallableObject
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException();

        public override int GetHashCode() => throw new InvalidOperationException();

        public override string ToString() => throw new InvalidOperationException();
    }

    private sealed class Values(params Value[] values) : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(values[(int)arguments[0].Number - 1]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
