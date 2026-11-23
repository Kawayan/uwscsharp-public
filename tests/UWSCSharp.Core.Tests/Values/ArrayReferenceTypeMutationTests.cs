using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ArrayReferenceTypeMutationTests
{
    [Theory]
    [InlineData("SETCLEAR(value,7)", "7")]
    [InlineData("SHIFTARRAY(value,1)", "")]
    [InlineData("RESIZE(value,2)", "2")]
    [InlineData("QSORT(value)", "1")]
    public async Task BulkUpdatesKeepReferenceTypeAndIndependentCopies(
        string operation, string first)
    {
        foreach (ushort referenceType in new ushort[] { 0x400c, 0x6003 })
        {
            foreach ((string target, string setup) in new[]
            {
                ("value", ""),
                ("value[]", ""),
                ("table[\"item\"]", "HASHTBL table\ntable[\"item\"]=value"),
                ("outer[0]", "DIM outer[0]\nouter[0]=value")
            })
            {
                Value source = Value.FromArray(UwscArray.FromValues(
                    [Value.Numeric(2), Value.Numeric(1)]).WithNativeElementType(3)) with
                {
                    NativeReferenceType = referenceType
                };
                List<string> printed = [];
                string script = $$"""
                    value=CREATEOLEOBJ("array").Values
                    saved=value
                    {{setup}}
                    {{operation.Replace("value", target, StringComparison.Ordinal)}}
                    changed={{(target == "value[]" ? "value" : target)}}
                    PRINT VARTYPE(changed)
                    PRINT VARTYPE(saved)
                    PRINT saved[0]
                    PRINT changed[0]
                    """;
                await new Interpreter(windows: new Automation(source)).RunAsync(script, printed.Add);
                Assert.Equal([referenceType.ToString(), referenceType.ToString(), "2", first], printed);
                Assert.Equal(2, source.AsArray().Get(0).Number);
            }
        }
    }

    [Theory]
    [InlineData("keys", "labels", "")]
    [InlineData("table[\"keys\"]", "table[\"labels\"]",
        "HASHTBL table\ntable[\"keys\"]=keys\ntable[\"labels\"]=labels")]
    [InlineData("rows[0]", "rows[1]", "DIM rows[1]\nrows[0]=keys\nrows[1]=labels")]
    public async Task LinkedSortKeepsEachReferenceTypeAndOriginalCopies(
        string keysTarget, string labelsTarget, string setup)
    {
        Value keys = Value.FromArray(UwscArray.FromValues(
            [Value.Numeric(2), Value.Numeric(1)]).WithNativeElementType(3)) with
        {
            NativeReferenceType = 0x400c
        };
        Value labels = Value.FromArray(UwscArray.FromValues(
            [Value.String("b"), Value.String("a")]).WithNativeElementType(8)) with
        {
            NativeReferenceType = 0x6008
        };
        string script = $$"""
            probe=CREATEOLEOBJ("array")
            keys=probe.Values
            labels=probe.Labels
            savedKeys=keys
            savedLabels=labels
            {{setup}}
            QSORT({{keysTarget}},0,{{labelsTarget}})
            changedKeys={{keysTarget}}
            changedLabels={{labelsTarget}}
            PRINT VARTYPE(changedKeys)
            PRINT VARTYPE(changedLabels)
            PRINT JOIN(changedKeys,",")
            PRINT JOIN(changedLabels,",")
            PRINT VARTYPE(savedKeys)
            PRINT VARTYPE(savedLabels)
            PRINT JOIN(savedKeys,",")
            PRINT JOIN(savedLabels,",")
            """;
        List<string> printed = [];
        await new Interpreter(windows: new Automation(keys, labels))
            .RunAsync(script, printed.Add);
        Assert.Equal(["16396", "24584", "1,2", "a,b",
            "16396", "24584", "2,1", "b,a"], printed);
        Assert.Equal(2, keys.AsArray().Get(0).Number);
        Assert.Equal("b", labels.AsArray().Get(0).Text);
    }

    private sealed class Automation(Value source, Value? linked = null) : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                name == BuiltinFunctionNames.VarType
                    ? VariantTypeRequest.Query(arguments[0]) : Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                name == "Labels" ? linked ?? source : source);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
