using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AdapterCancellationTests
{
    private sealed class Adapter(CancellationTokenSource cancellation)
        : IWindowsAutomation, IWindowsAutomationSession,
        IScriptFiles, IScriptFileSession, INativeDllScalarInvoker
    {
        public int Invocations
        {
            get; private set;
        }

        public int SnapshotReads
        {
            get; private set;
        }

        public int Disposals
        {
            get; private set;
        }

        public int WorkerForks
        {
            get; private set;
        }

        public IWindowsAutomationSession Fork()
        {
            WorkerForks++;
            throw new InvalidOperationException("テスト中にTHREADの開始が要求されました。");
        }

        public bool CancelInSnapshot
        {
            get; init;
        }

        public IReadOnlyList<string> AllItems
        {
            get
            {
                SnapshotReads++;
                if (CancelInSnapshot)
                {
                    cancellation.Cancel();
                }

                return ["cancelled"];
            }
        }

        public IReadOnlyList<string> DirectoryFiles => AllItems;

        public WindowsImageResult ImageResult
        {
            get
            {
                SnapshotReads++;
                if (CancelInSnapshot)
                {
                    cancellation.Cancel();
                }

                return WindowsImageResult.Empty;
            }
        }

        IWindowsAutomationSession IWindowsAutomation.CreateSession() => this;

        IScriptFileSession IScriptFiles.CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            if (name == BuiltinFunctionNames.CreateOleObj)
            {
                return ValueTask.FromResult(Value.FromObject(this));
            }
            Invocations++;
            // 取消を通知しても正常復帰するadapterで復帰境界を検査します。
            if (!CancelInSnapshot)
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken token)
        {
            Assert.Same(this, target.AsObject());
            Invocations++;
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(
            Value target, string name, IReadOnlyList<WindowsMemberArgument> arguments,
            Value? assigned, CancellationToken token)
        {
            Assert.Same(this, target.AsObject());
            Invocations++;
            return ValueTask.FromResult(new WindowsMemberResult(Value.Numeric(1),
                [.. arguments.Select(argument => argument.Value)]));
        }

        public object InvokeNativeDll(NativeDllDeclaration declaration,
            string workingDirectory, object?[] arguments, CancellationToken token,
            ushort? controlWord)
        {
            if (declaration.Name == "RequestStop")
            {
                cancellation.Cancel();
                return 1;
            }
            Assert.Equal("Pair", declaration.Name);
            arguments[0] = 101;
            arguments[1] = 109;
            return 42;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingHost(CancellationTokenSource cancellation)
        : IScriptHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("PRINT 1")]
    [InlineData("PRINT")]
    [InlineData("SLEEP(1)")]
    [InlineData("TRY\nSLEEP(1)\nEXCEPT\nPRINT 2\nENDTRY")]
    public async Task LastHostCallCannotTurnCancellationIntoSuccess(string script)
    {
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync(script,
                new CancellingHost(cancellation), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task GetItemCancellationPublishesAdapterStateForFinally()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync("""
                OPTION OPTFINALLY
                TRY
                    GETITEM(1, ITM_BTN)
                FINALLY
                    PRINT ALL_ITEM_LIST[0]
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Equal(["cancelled"], output);
        Assert.Equal(1, adapter.SnapshotReads);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("GETITEM(1, ITM_BTN)", true)]
    [InlineData("CHKIMG(\"fixture.bmp\")", true)]
    [InlineData("GETDIR(\"fixture\")", false)]
    public async Task SnapshotReadCancellationStillPropagates(
        string script, bool windows)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation)
        {
            CancelInSnapshot = true
        };
        var runner = new Interpreter(windows: windows ? adapter : null,
            files: windows ? null : adapter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(script, _ => { }, cancellation.Token));
        Assert.Equal(1, adapter.SnapshotReads);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("GETITEM(1, ITM_BTN)", true)]
    [InlineData("CHKIMG(\"fixture.bmp\")", true)]
    [InlineData("CHKBTN(1, \"target\")", true)]
    [InlineData("GETDIR(\"fixture\")", false)]
    [InlineData("FOPEN(\"fixture\")", false)]
    public async Task LastAdapterCallHonorsCancellationAndItemSideEffects(
        string script, bool windows)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: windows ? adapter : null,
            files: windows ? null : adapter);
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(script, output.Add, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(output);
        Assert.Equal(script.StartsWith("GETITEM", StringComparison.Ordinal) ? 1 : 0, adapter.SnapshotReads);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("FOPEN(StopInside(),F_READ)")]
    [InlineData("GETID(StopInside())")]
    public async Task AdapterDoesNotStartAfterStopDuringDllWriteback(string call)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: adapter, files: adapter);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(StoppedDllWritebackScript("DIM value=" + call), output.Add,
                cancellation.Token));

        Assert.Equal(0, adapter.Invocations);
        Assert.Equal("7,101|109|2", Assert.Single(output));
    }

    [Theory]
    [InlineData("DIM value=fixtureObject.Run(StopInside())")]
    [InlineData("DIM value=fixtureObject.Run(VAR output,StopInside())")]
    [InlineData("DIM value=fixtureObject.Run(Arg=StopInside())")]
    [InlineData("fixtureObject.Value=StopInside()")]
    public async Task ComMemberDoesNotStartAfterStopDuringDllWriteback(string statement)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: adapter);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(StoppedComMemberWritebackScript(statement), output.Add,
                cancellation.Token));

        Assert.Equal(0, adapter.Invocations);
        Assert.Equal("7,101|109|2", Assert.Single(output));
    }

    [Theory]
    [InlineData("EVAL(StopInside())", "123")]
    [InlineData("EVAL(StopInside())", "\"evalValue:=99\"")]
    [InlineData("EVAL(\"CALL <#DBL>fixture<#DBL>=StopInside()+<#DBL><#DBL>\")", "123")]
    public async Task EvalKeepsOuterResultWhenItsInputStops(string call,
        string stopResult)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: adapter);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(StoppedEvalWritebackScript(call, stopResult),
                output.Add, cancellation.Token));

        Assert.Equal(0, adapter.Invocations);
        Assert.Equal("7,101|109|2|11", Assert.Single(output));
    }

    private static string StoppedEvalWritebackScript(string call,
        string stopResult) =>
        StoppedDllWritebackScript("DIM value=" + call, "PUBLIC evalValue=11")
            .Replace("RESULT=\"fixture\"", "RESULT=" + stopResult,
                StringComparison.Ordinal)
            .Replace("+number+\"|\"+reads", "+number+\"|\"+reads+\"|\"+evalValue",
                StringComparison.Ordinal);

    [Theory]
    [InlineData("THREAD Worker(StopInside())", "value")]
    [InlineData("THREAD StopInside().Worker()", "value")]
    [InlineData("DIM started=EVAL(\"THREAD Worker(StopInside())\")", "value")]
    [InlineData("THREAD Worker(StopInside())", "value[]")]
    public async Task ThreadDoesNotStartAfterStopDuringDllWriteback(
        string statement, string parameter)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: adapter);
        var output = new List<string>();
        string source = StoppedDllWritebackScript(statement) + $"""

            PROCEDURE Worker({parameter})
            FEND
            """;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(source, output.Add, cancellation.Token));

        Assert.Equal(0, adapter.WorkerForks);
        Assert.Equal("7,101|109|2", Assert.Single(output));
    }

    [Theory]
    [InlineData("CTRLTRD(StopInside(),TRD_QUIT)", "\"fixture\"")]
    [InlineData("CTRLTRD(0,StopInside())", "\"fixture\"")]
    [InlineData("CTRLTRD(StopInside(),1/0)", "0")]
    public async Task CtrlTrdKeepsWritebackIndexWhenArgumentStops(
        string call, string stopResult)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancellation);
        var runner = new Interpreter(windows: adapter);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(StoppedDllWritebackScript(call)
                .Replace("RESULT=\"fixture\"", "RESULT=" + stopResult,
                    StringComparison.Ordinal), output.Add, cancellation.Token));

        Assert.Equal("7,101|109|2", Assert.Single(output));
    }

    private static string StoppedComMemberWritebackScript(string statement) =>
        StoppedDllWritebackScript(statement, """
            PUBLIC fixtureObject,output=11
            fixtureObject=CREATEOLEOBJ("fixture")
            """);

    private static string StoppedDllWritebackScript(string statement,
        string setup = "") => $$"""
        OPTION OPTFINALLY
        DEF_DLL Pair(VAR int,VAR int):int:fixture.dll
        DEF_DLL RequestStop():int:fixture.dll
        PUBLIC reads=0
        DIM values[1]=7,8
        DIM number=19
        {{setup}}
        TRY
            PRINT Pair(values[Choose()],number)
        FINALLY
            PRINT values[0]+","+values[1]+"|"+number+"|"+reads
        ENDTRY
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                RESULT=1
                {{statement}}
            ELSE
                RESULT=0
            ENDIF
        FEND
        FUNCTION StopInside()
            RESULT="fixture"
            RequestStop()
        FEND
        """;
}
