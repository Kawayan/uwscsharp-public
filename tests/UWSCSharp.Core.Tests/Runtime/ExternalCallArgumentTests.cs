using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Theory]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("SPLIT(\"x\")")]
    [InlineData("table")]
    public async Task UnsupportedArgumentTextIsCatchableWithoutChangingParent(string argument)
    {
        Write("child.uws", "PRINT \"unexpected\"");
        string main = Write("main.uws", "HASHTBL table\nTRY\nCALL child("
            + argument + ")\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\nPRINT PARAM_STR[0]");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), default, new ScriptRunOptions(["root"]));
        Assert.Equal(["3", "root"], output);
    }

    [Fact]
    public async Task ConversionFailureStopsLaterArgumentsAndDoesNotEnterChild()
    {
        Write("child.uws", "PRINT \"unexpected\"");
        string main = Write("main.uws", """
            TRY
                CALL child(Tick("first"),NULL,Tick("last"))
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            PRINT PARAM_STR[0]
            FUNCTION Tick(value)
                PRINT value
                RESULT=value
            FEND
            """);
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), default, new ScriptRunOptions(["root"]));
        Assert.Equal(["first", "2", "root"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendObjectAndErrorValuesAreRejectedWithoutImplicitConversion(bool errorValue)
    {
        var probe = new DisplayProbe();
        var files = new ArgumentFiles(errorValue
            ? Value.FromError(17) : Value.FromObject(probe));
        Write("child.uws", "PRINT \"unexpected\"");
        string main = Write("main.uws", """
            PUBLIC argument=FOPEN("mock",F_READ)
            TRY
                CALL child(argument)
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            PRINT PARAM_STR[0]
            """);
        List<string> output = [];
        await new Interpreter(files: files).RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add), default, new ScriptRunOptions(["root"]));
        Assert.Equal(["3", "root"], output);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(1, files.Disposals);
    }

    private sealed class DisplayProbe
    {
        internal int Calls
        {
            get; private set;
        }

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("暗黙変換を呼び出しました。");
        }
    }

    private sealed class ArgumentFiles(Value value) : IScriptFiles, IScriptFileSession
    {
        internal int Disposals
        {
            get; private set;
        }

        public IScriptFileSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(value);

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
