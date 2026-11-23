using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComErrorControlTests
{
    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal Exception Failure = new InvalidOperationException("COM failure");
        internal bool KnownCom = true;
        internal bool ReportScript = true;
        internal bool CreateFails;
        internal Exception? ClassificationFailure;
        internal Exception? DisposeFailure;
        internal int Classifications;
        internal int ReferenceCalls;
        internal int PropertyWrites;

        public IWindowsAutomationSession CreateSession() => this;

        public IWindowsAutomationSession Fork() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            CreateFails ? ValueTask.FromException<Value>(Failure)
                : ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken token)
        {
            if (name == "Success")
            {
                PropertyWrites += assigned.HasValue ? 1 : 0;
                return ValueTask.FromResult(Value.Numeric(7));
            }

            return ValueTask.FromException<Value>(Failure);
        }

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(
            Value target, string name,
            IReadOnlyList<WindowsMemberArgument> arguments,
            Value? assigned, CancellationToken token)
        {
            ReferenceCalls++;
            return ValueTask.FromException<WindowsMemberResult>(Failure);
        }

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(
            Value target, CancellationToken token) =>
            ValueTask.FromException<IReadOnlyList<Value>>(Failure);

        public bool IsComError(Exception failure)
        {
            Classifications++;
            if (ClassificationFailure is not null)
            {
                throw ClassificationFailure;
            }

            return KnownCom && ReferenceEquals(failure, Failure);
        }

        public string? GetObjectScriptErrorMessage(Exception failure) =>
            ReportScript && ReferenceEquals(failure, Failure) ? failure.Message : null;

        public string? GetScriptErrorMessage(string name, Exception failure) =>
            GetObjectScriptErrorMessage(failure);

        public ValueTask DisposeAsync() => DisposeFailure is null
            ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
    }

    [Fact]
    public async Task CommandsNeedNoWindowsAdapterAndStartWithFalseFlag()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PRINT COM_ERR_FLG
            COM_ERR_RET
            PRINT COM_ERR_FLG
            IF TRUE THEN COM_ERR_IGN ELSE COM_ERR_RET
            PRINT COM_ERR_FLG
            COM_ERR_RET
            """, output.Add);
        Assert.Equal(["0", "0", "0"], output);
    }

    [Fact]
    public async Task CalledScriptSharesComErrorFlagAndSuppression()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("uwsc-com-call-");
        try
        {
            string child = Path.Combine(directory.FullName, "child.uws");
            File.WriteAllText(child, """
                PRINT COM_ERR_FLG
                called = CREATEOLEOBJ("fixture")
                PRINT called.Failure()
                COM_ERR_IGN
                PRINT COM_ERR_FLG
                COM_ERR_RET
                """);
            string main = Path.Combine(directory.FullName, "main.uws");
            File.WriteAllText(main, """
                root = CREATEOLEOBJ("fixture")
                COM_ERR_IGN
                root.Failure()
                CALL child
                PRINT COM_ERR_FLG
                TRY
                    root.Failure()
                    PRINT "unexpected"
                EXCEPT
                    PRINT COM_ERR_FLG
                ENDTRY
                """);
            List<string> output = [];
            var interpreter = new Interpreter(windows: new Adapter());
            await interpreter.RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));
            Assert.Equal(["1", "-999999", "0", "0", "1"], output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task WorkerComErrorStateStartsFreshAndDoesNotResetParent()
    {
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: new Adapter()).RunAsync("""
            PUBLIC done = FALSE
            o = CREATEOLEOBJ("fixture")
            COM_ERR_IGN
            o.Failure()
            THREAD Worker()
            WHILE !done
                SLEEP(0.001)
            WEND
            PRINT COM_ERR_FLG
            PRINT o.Failure()
            PROCEDURE Worker()
                localObject = CREATEOLEOBJ("fixture")
                PRINT COM_ERR_FLG
                TRY
                    localObject.Failure()
                    PRINT "unexpected"
                EXCEPT
                    PRINT COM_ERR_FLG
                ENDTRY
                COM_ERR_IGN
                PRINT COM_ERR_FLG
                COM_ERR_RET
                done = TRUE
            FEND
            """, output.Enqueue, timeout.Token);
        string[] actual = [.. output];
        Assert.Equal(["0", "1", "0", "1", "-999999"], actual);
    }

    [Fact]
    public async Task FailureValueContinuesExpressionAndFlagOnlyResetsOnIgnore()
    {
        List<string> output = [];
        await new Interpreter(windows: new Adapter()).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            COM_ERR_IGN
            PRINT 10+o.Failure()
            PRINT COM_ERR_FLG
            PRINT o.Success
            PRINT COM_ERR_FLG
            COM_ERR_RET
            PRINT COM_ERR_FLG
            COM_ERR_IGN
            PRINT COM_ERR_FLG
            """, output.Add);
        Assert.Equal(["-999989", "1", "7", "1", "1", "0"], output);
    }

    [Fact]
    public async Task ReusingInterpreterStartsWithIndependentComState()
    {
        var interpreter = new Interpreter(windows: new Adapter());
        List<string> output = [];
        await interpreter.RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            COM_ERR_IGN
            o.Failure()
            """, output.Add);
        await interpreter.RunAsync("""
            PRINT COM_ERR_FLG
            DIM o=CREATEOLEOBJ("fixture")
            TRY
                o.Failure()
                PRINT "still-ignored"
            EXCEPT
                PRINT COM_ERR_FLG
            ENDTRY
            """, output.Add);
        Assert.Equal(["0", "1"], output);
    }

    [Theory]
    [InlineData("PRINT o.Failure()", "-999999")]
    [InlineData("PRINT o.Failure", "-999999")]
    [InlineData("o.Failure=12", null)]
    [InlineData("PRINT LENGTH(o)", "-999999")]
    [InlineData("FOR value IN o\nPRINT 99\nNEXT", null)]
    public async Task KnownComFailuresContinueAtEachOperationBoundary(
        string operation, string? result)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            "DIM o=CREATEOLEOBJ(\"fixture\")\nCOM_ERR_IGN\nTRY\n"
            + operation + "\nPRINT COM_ERR_FLG\nEXCEPT\nPRINT 99\nENDTRY",
            output.Add);
        Assert.Equal(result is null ? ["1"] : new[] { result, "1" }, output);
        Assert.Equal(1, adapter.Classifications);
    }

    [Fact]
    public async Task CreationFailureReturnsSentinel()
    {
        var adapter = new Adapter { CreateFails = true };
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            COM_ERR_IGN
            PRINT CREATEOLEOBJ("fixture")
            PRINT COM_ERR_FLG
            """, output.Add);
        Assert.Equal(["-999999", "1"], output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("COM_ERR_IGN\nCOM_ERR_IGN\nCOM_ERR_RET\n")]
    public async Task UnignoredComErrorSetsFlagAndReachesExcept(string setup)
    {
        List<string> output = [];
        await new Interpreter(windows: new Adapter()).RunAsync(
            "DIM o=CREATEOLEOBJ(\"fixture\")\n" + setup
            + "TRY\no.Failure()\nEXCEPT\nPRINT COM_ERR_FLG\n"
            + "ENDTRY\nPRINT COM_ERR_FLG", output.Add);
        Assert.Equal(["1", "1"], output);
    }

    [Fact]
    public async Task SuppressedReferenceFailureDoesNotReevaluateOrWriteTargets()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC evaluations=0
            DIM o=CREATEOLEOBJ("fixture")
            DIM values[0]=7
            COM_ERR_IGN
            PRINT o.Change(VAR values[Index()],VAR o.Success)
            PRINT values[0]
            PRINT evaluations
            PRINT COM_ERR_FLG
            FUNCTION Index()
                evaluations=evaluations+1
                RESULT=0
            FEND
            """, output.Add);
        Assert.Equal(["-999999", "7", "1", "1"], output);
        Assert.Equal(1, adapter.ReferenceCalls);
        Assert.Equal(0, adapter.PropertyWrites);
    }

    [Fact]
    public async Task ScriptFailureOutsideComLeavesFlagFalse()
    {
        List<string> output = [];
        await new Interpreter(windows: new Adapter()).RunAsync("""
            COM_ERR_IGN
            TRY
                DIM values[0]
                PRINT values[1]
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT COM_ERR_FLG
            """, output.Add);
        Assert.Equal(["caught", "0"], output);
    }

    [Fact]
    public async Task ReadOnlyFlagCannotBePassedAsWritableComArgument()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            COM_ERR_IGN
            TRY
                o.Change(VAR COM_ERR_FLG)
            EXCEPT
                PRINT COM_ERR_FLG
            ENDTRY
            """, output.Add);
        Assert.Equal(["0"], output);
        Assert.Equal(0, adapter.ReferenceCalls);
        Assert.Equal(0, adapter.Classifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonComFailureKeepsExistingScriptOrHostClassification(
        bool reports)
    {
        var adapter = new Adapter { KnownCom = false, ReportScript = reports };
        List<string> output = [];
        string source = "DIM o=CREATEOLEOBJ(\"fixture\")\nCOM_ERR_IGN\n"
            + "TRY\no.Failure()\nEXCEPT\nPRINT COM_ERR_FLG\nENDTRY";
        if (reports)
        {
            await new Interpreter(windows: adapter).RunAsync(source, output.Add);
            Assert.Equal(["0"], output);
        }
        else
        {
            Exception failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new Interpreter(windows: adapter).RunAsync(source, output.Add));
            Assert.Same(adapter.Failure, failure);
            Assert.Empty(output);
        }
    }

    [Fact]
    public async Task CancellationBypassesComClassification()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            Failure = new OperationCanceledException(cancellation.Token)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "DIM o=CREATEOLEOBJ(\"fixture\")\nCOM_ERR_IGN\no.Failure()",
                _ => { }, cancellation.Token));
        Assert.Equal(0, adapter.Classifications);
    }

    [Fact]
    public async Task ClassificationFailureRemainsHostFailure()
    {
        var failure = new FormatException("classifier");
        var adapter = new Adapter { ClassificationFailure = failure };
        Exception actual = await Assert.ThrowsAsync<FormatException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "DIM o=CREATEOLEOBJ(\"fixture\")\nCOM_ERR_IGN\n"
                + "TRY\no.Failure()\nEXCEPT\nPRINT 99\nENDTRY", _ => { }));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task DisposalFailureIsNeverSuppressed()
    {
        var failure = new InvalidOperationException("dispose");
        var adapter = new Adapter { DisposeFailure = failure };
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter(windows: adapter).RunAsync("COM_ERR_IGN", _ => { }));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData("COM_ERR_IGN()")]
    [InlineData("COM_ERR_RET 1")]
    [InlineData("DIM COM_ERR_IGN")]
    [InlineData("COM_ERR_FLG=FALSE")]
    [InlineData("PUBLIC COM_ERR_FLG=0")]
    [InlineData("DIM COM_ERR_FLG[1]")]
    public void RejectsOperandsAndFlagRedefinition(string source) =>
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));

    [Fact]
    public void StaticAnalysisRecognizesCommandsWithoutUncheckedNodes()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
            "COM_ERR_IGN\nPRINT COM_ERR_FLG\nCOM_ERR_RET");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.UncheckedSyntaxNodes);
    }
}
