using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリだけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllWritebackCancellationTests
{
    private const string Declarations = """
        OPTION OPTFINALLY
        DEF_DLL Pair(VAR int,VAR int):int:kernel32.dll
        DEF_DLL RequestStop():int:kernel32.dll
        DEF_DLL Nested(VAR int):int:kernel32.dll
        DEF_DLL Tick():int:kernel32.dll
        """ + "\n";

    public static IEnumerable<object[]> ResultExpressionRegressionCases()
    {
        foreach (int mode in new[] { 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38 })
        {
            yield return [mode, false];
            yield return [mode, true];
        }
    }

    [Theory]
    [MemberData(nameof(ResultExpressionRegressionCases))]
    public async Task StoppedResultExpressionKeepsValueAndType(int mode, bool stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            ResultExpressionRegressionScript(mode, stop), output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            await run;
        }
        var expected = new List<string>();
        if (mode == 34)
        {
            expected.Add("probe=0|5");
        }
        expected.Add("inner=" + (stop ? 1 : 0));
        if (!stop && mode is not (33 or 34))
        {
            expected.Add("outer-caught");
        }
        string value = !stop ? "123" : mode is 28 or 29 or 35 ? "11"
            : mode is 32 or 38 ? "" : "1";
        int type = stop && mode is 32 or 36 or 37 or 38 ? 258 : 5;
        expected.Add($"outer-finally=1|{value}|97|29,31|101|{type}");
        if (!stop)
        {
            if (mode is 33 or 34)
            {
                expected.Add("caught");
                expected.Add("recovered=101,109");
            }
            else
            {
                expected.Add("42");
            }
        }
        expected.Add((mode is 33 or 34 ? "101,8" : "7,101")
            + "|109|2|97|29,31|101");
        Assert.Equal(expected, output);
        Assert.Equal(!stop && mode is 33 or 34 ? 2 : 1, loader.PairCalls);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedExpressionDoesNotCallShadowingModuleDll(bool globalRoutine)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            ShadowingModuleDllScript(globalRoutine), output.Add, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, loader.TickCalls);
        Assert.Equal("7,101|109|2", Assert.Single(output));
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    private static string ShadowingModuleDllScript(bool globalRoutine) =>
        """
            OPTION OPTFINALLY
            DEF_DLL Pair(VAR int,VAR int):int:kernel32.dll
            DEF_DLL RequestStop():int:kernel32.dll
            PUBLIC reads=0
            DIM values[1]=7,8
            DIM number=19
            TRY
                PRINT Pair(values[Box.Choose()],number)
            FINALLY
                PRINT values[0]+","+values[1]+"|"+number+"|"+reads
            ENDTRY
            MODULE Box
                DEF_DLL Tick():int:kernel32.dll
                FUNCTION Choose()
                    reads=reads+1
                    IFB reads=2
                        RESULT=1
                        DIM computed=StopInside()+Tick()
                    ELSE
                        RESULT=0
                    ENDIF
                FEND
                FUNCTION StopInside()
                    RESULT=1
                    RequestStop()
                FEND
            ENDMODULE
            """ + "\n" + (globalRoutine ? """
            FUNCTION Tick()
                PRINT "global-tick"
                RESULT=0
            FEND
            """ : "");

    [Theory]
    [InlineData("Consume(StopInside())", false)]
    [InlineData("Box.Consume(StopInside())", false)]
    [InlineData("GLOBAL.Consume(StopInside())", false)]
    [InlineData(".Consume(StopInside())", false)]
    [InlineData("Consume(StopInside())", true)]
    [InlineData("GLOBAL.Consume(StopInside())", true)]
    public async Task StopDuringDllArgumentPreventsInvocation(string call,
        bool invalidValue)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            StoppedDllArgumentScript(call, invalidValue), output.Add, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, loader.ConsumeCalls);
        Assert.Equal("7,101|109|2", Assert.Single(output));
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    private static string StoppedDllArgumentScript(string call,
        bool invalidValue)
    {
        const string declaration = "DEF_DLL Consume(int):int:kernel32.dll";
        string body = "DIM computed=" + call;
        if (call.StartsWith('.'))
        {
            body = "WITH Box\n" + body + "\nENDWITH";
        }
        string source = ShadowingModuleDllScript(globalRoutine: false)
            .Replace("DEF_DLL Tick():int:kernel32.dll", declaration,
                StringComparison.Ordinal)
            .Replace("DIM computed=StopInside()+Tick()", body,
                StringComparison.Ordinal);
        if (invalidValue)
        {
            const string beforeStop = "RESULT=1\n        RequestStop()";
            Assert.Contains(beforeStop, source);
            source = source.Replace(beforeStop,
                "RESULT=\"bad\"\n        RequestStop()", StringComparison.Ordinal);
        }
        return call.StartsWith("GLOBAL.", StringComparison.Ordinal)
            ? source.Replace("OPTION OPTFINALLY\n",
                "OPTION OPTFINALLY\n" + declaration + "\n",
                StringComparison.Ordinal)
            : source;
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("\"bad\"")]
    public async Task StopDuringDllArrayCoordinateKeepsOuterResult(string result)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            StoppedDllArrayArgumentScript(result), output.Add, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, loader.ConsumeCalls);
        Assert.Equal("7,101|109|2", Assert.Single(output));
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    private static string StoppedDllArrayArgumentScript(string result)
    {
        const string call = "ConsumeArray(innerValues[StopInside()])";
        return StoppedDllArgumentScript(call, invalidValue: false)
            .Replace("DEF_DLL Consume(int):int", "DEF_DLL ConsumeArray(int[]):int",
                StringComparison.Ordinal)
            .Replace("DIM computed=" + call, "DIM innerValues[1]=3,4\nDIM computed=" + call,
                StringComparison.Ordinal)
            .Replace("RESULT=1\n        RequestStop()",
                "RESULT=" + result + "\n        RequestStop()", StringComparison.Ordinal);
    }

    private static string ResultExpressionRegressionScript(int mode, bool stop)
    {
        string source = NestedRoutineCopybackScript(stop, mode is not (33 or 34));
        const string inner = "InnerReference(firstValue,localValues[cursor],thirdValue)";
        string expression = mode switch
        {
            28 => inner + "+10",
            29 => "10+" + inner,
            30 => inner + "+SideOperand()",
            31 => "ABS(" + inner + ")",
            32 => "VARTYPE(" + inner + ")",
            35 => inner + "+ABS(-10)",
            36 => inner + "+VARTYPE(0)",
            37 => inner + "+VAL(\"2\")",
            38 => "VAL(" + inner + ")",
            _ => inner
        };
        string assignment = "innerResult=" + inner;
        source = source.Replace(assignment,
            "IFB reads=0\n" + assignment + "\nELSE\ninnerResult="
                + expression + "\nENDIF", StringComparison.Ordinal)
            .Replace("+thirdValue\n", "+thirdValue+\"|\"+VARTYPE(innerResult)\n", StringComparison.Ordinal);
        const string choose = "Choose(argument,otherValues[0],lastArgument)";
        if (mode == 33)
        {
            source = source.Replace("values[" + choose + "]",
                "values[VARTYPE(" + choose + ")-5]", StringComparison.Ordinal);
        }
        if (mode == 34)
        {
            source = source.Replace("values[" + choose + "]",
                "values[ProbeResult(" + choose + ")]", StringComparison.Ordinal)
                + "FUNCTION ProbeResult(value)\nPRINT \"probe=\"+value+\"|\"+VARTYPE(value)\nRESULT=value\nFEND\n";
        }
        return source + "FUNCTION SideOperand()\nPRINT \"operand-ran\"\nRESULT=0\nFEND\n";
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NestedRoutineCopybackFailureUsesCallerHandler(bool stop, bool handler)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            NestedRoutineCopybackScript(stop, handler), output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            await run;
        }
        var expected = new List<string> { "inner=" + (stop ? 1 : 0) };
        if (!stop && handler)
        {
            expected.Add("outer-caught");
        }
        expected.Add("outer-finally=1|" + (stop ? 1 : 123) + "|97|29,31|101");
        if (!stop)
        {
            if (handler)
            {
                expected.Add("42");
            }
            else
            {
                expected.Add("caught");
                expected.Add("recovered=101,109");
            }
        }
        expected.Add((stop || handler ? "7,101" : "101,8")
            + "|109|2|97|29,31|101");
        Assert.Equal(expected, output);
        Assert.Equal(!stop && !handler ? 2 : 1, loader.PairCalls);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FourRoutineFramesCompleteWritebackInOrder(bool stop,
        bool handler)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            FourRoutineCopybackScript(stop, handler), output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            await run;
        }

        int result = stop ? 1 : 123;
        var expected = new List<string>
        {
            "inner=" + (stop ? 1 : 0),
            $"third-finally=1|{result}|97|101",
            $"second-finally=1|{result}|101|101"
        };
        if (handler)
        {
            expected.Add("outer-caught");
        }
        expected.Add($"outer-finally=1|{result}|105|29,31|101");
        if (handler)
        {
            expected.Add("42");
        }
        else if (!stop)
        {
            expected.AddRange(["caught", "recovered=101,109"]);
        }
        expected.Add((stop || handler ? "7,101" : "101,8")
            + "|109|2|105|29,31|101");
        Assert.Equal(expected, output);
        Assert.Equal(!stop && !handler ? 2 : 1, loader.PairCalls);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task FourRoutineFramesWriteBackAfterStopWithoutOptFinally()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new Clock();
        using var loader = new Loader(timeout, clock);
        var output = new ConcurrentQueue<string>();
        await CreateInterpreter(loader, clock).RunAsync(
            FourRoutineStopWithoutOptFinallyScript(), output.Enqueue, timeout.Token);

        Assert.Equal("7,101|109|2|71|29,31|79", Assert.Single(output));
        Assert.Equal(1, loader.PairCalls);
        AssertCompletedChildWritebackTimer(clock);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(30, "1", 5)]
    [InlineData(31, "1", 5)]
    [InlineData(32, "", 258)]
    [InlineData(35, "11", 5)]
    [InlineData(36, "1", 258)]
    [InlineData(37, "1", 258)]
    [InlineData(38, "", 258)]
    public async Task StoppedResultExpressionWritesBackWithoutOptFinally(
        int mode, string result, int resultType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new Clock();
        using var loader = new Loader(timeout, clock);
        var output = new ConcurrentQueue<string>();
        await CreateInterpreter(loader, clock).RunAsync(
            ResultExpressionWithoutOptFinallyScript(mode), output.Enqueue, timeout.Token);

        Assert.Equal($"7,101|109|2|71|29,31|79|{result}|{resultType}",
            Assert.Single(output));
        Assert.Equal(1, loader.PairCalls);
        AssertCompletedChildWritebackTimer(clock);
        Assert.Equal(1, loader.Frees);
    }

    private static void AssertCompletedChildWritebackTimer(Clock clock)
    {
        Timer timer = Assert.Single(clock.Timers);
        // 子スレッドの取消コールバックより先に書戻しが完了する場合があります。
        TimeSpan[] allowedDueTimes = [Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(5)];
        Assert.Contains(timer.DueTime, allowedDueTimes);
        Assert.False(timer.Fired);
        Assert.True(timer.Disposed);
    }

    private static string FourRoutineStopWithoutOptFinallyScript() =>
        StoppedChildCopybackScript(FourRoutineCopybackScript(stop: true, handler: false));

    private static string ResultExpressionWithoutOptFinallyScript(int mode)
    {
        const string local = "DIM innerResult=123,localValues[1]=29,31";
        string source = ResultExpressionRegressionScript(mode, stop: true);
        Assert.Contains(local, source);
        source = source.Replace(local, "DIM localValues[1]=29,31",
            StringComparison.Ordinal);
        return StoppedChildCopybackScript(source,
            "PUBLIC innerResult=123\n", "+\"|\"+innerResult+\"|\"+VARTYPE(innerResult)");
    }

    private static string StoppedChildCopybackScript(string source,
        string declarations = "", string snapshotSuffix = "")
    {
        const string leaf = "FUNCTION InnerReference";
        int routines = source.IndexOf(leaf, StringComparison.Ordinal);
        Assert.True(routines >= 0);
        return Declarations.Replace("OPTION OPTFINALLY\n", "",
                StringComparison.Ordinal)
            + declarations
            + $$"""
                PUBLIC reads=0,cursor=0
                PUBLIC values[1]=7,8
                PUBLIC otherValues[1]=29,31
                PUBLIC number=19,argument=23,lastArgument=37
                THREAD Worker()
                WHILE number=19
                    SLEEP(0)
                WEND
                PRINT values[0]+","+values[1]+"|"+number+"|"+reads+"|"+argument+"|"+otherValues[0]+","+otherValues[1]+"|"+lastArgument{{snapshotSuffix}}
                PROCEDURE Worker()
                    TRY
                        PRINT Pair(values[Choose(argument,otherValues[0],lastArgument)],number)
                    FINALLY
                        PRINT "worker-finally"
                    ENDTRY
                FEND
                """ + "\n"
            + source[routines..].Replace("RequestStop()", "CTRLTRD(0,TRD_QUIT)",
                StringComparison.Ordinal);
    }

    private static string FourRoutineCopybackScript(bool stop, bool handler)
    {
        const string call = "InnerReference(firstValue,localValues[cursor],thirdValue)";
        string source = NestedRoutineCopybackScript(stop, handler);
        Assert.Contains(call, source);
        return source.Replace(call,
                "SecondReference(firstValue,thirdValue)",
                StringComparison.Ordinal)
            + PassReferenceThrough("SecondReference",
                "ThirdReference(firstValue,thirdValue)", "second", "")
            + PassReferenceThrough("ThirdReference",
                "InnerReference(firstValue,localValues[cursor],thirdValue)",
                "third", "DIM localValues[1]=29,31");
    }

    private static string PassReferenceThrough(string name, string call,
        string trace, string localArray) => $$"""
        FUNCTION {{name}}(VAR firstValue,VAR thirdValue)
            RESULT=1
            DIM returned=123
            {{localArray}}
            TRY
                returned={{call}}
                RESULT=returned
            FINALLY
                IFB reads=2
                    PRINT "{{trace}}-finally="+RESULT+"|"+returned+"|"+firstValue+"|"+thirdValue
                    firstValue=firstValue+4
                ENDIF
            ENDTRY
        FEND
        """ + "\n";

    private static string NestedRoutineCopybackScript(bool stop, bool handler)
    {
        string source = DeferredCopybackScript(0, stop, true)
            .Replace("Choose(argument,otherValues[cursor],lastArgument)",
                "Choose(argument,otherValues[0],lastArgument)", StringComparison.Ordinal)
            .Replace("FUNCTION Choose(VAR firstValue,VAR secondValue,VAR thirdValue)",
                "FUNCTION InnerReference(VAR firstValue,VAR secondValue,VAR thirdValue)", StringComparison.Ordinal);
        string opening = handler ? "TRY\n" : "";
        string caught = handler ? "EXCEPT\nPRINT \"outer-caught\"\nENDTRY\n" : "";
        return source + "FUNCTION Choose(VAR firstValue,VAR secondValue,VAR thirdValue)\n"
            + "RESULT=1\nDIM innerResult=123,localValues[1]=29,31\nTRY\n"
            + opening + "innerResult=InnerReference(firstValue,localValues[cursor],thirdValue)\n"
            + "RESULT=innerResult\n" + caught + "FINALLY\n"
            + "IFB reads=2\n"
            + "PRINT \"outer-finally=\"+RESULT+\"|\"+innerResult+\"|\"+firstValue+\"|\"+localValues[0]+\",\"+localValues[1]+\"|\"+thirdValue\n"
            + "ENDIF\nENDTRY\nFEND\n";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WritebackKeepsIndexRulesAndStopsAcrossNestedCalls(
        int mode)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock,
            cancelInPair: mode == 0, cancelInNested: mode == 2);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            IndexedScript(mode), output.Add, cancellation.Token);
        if (mode == 3)
        {
            await run;
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        string snapshot = mode == 3 ? "7,101|109|2|5,3,3"
            : $"101,8|109|{(mode == 0 ? 1 : 2)}|3,5,3";
        string[] expected = mode == 3 ? ["result=42", snapshot] : [snapshot];
        Assert.Equal(expected, output);
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(mode == 1 ? 1 : 0, loader.StopCalls);
        Assert.Equal(mode == 2 ? 1 : 0, loader.NestedCalls);
        Assert.Equal(mode == 3 ? 1 : 2, clock.Timers.Count);
        Assert.Equal(mode == 3 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5),
            clock.Timers[0].DueTime);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task CancelledIndexSkipsLoopAndCompletesRemainingWriteback()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock,
            expireOnTick: true, secondInput: 7);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                BoundedScript, output.Add, cancellation.Token));
        Assert.Equal(["101|109,8|2"], output);
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(0, loader.TickCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.Equal(2, clock.Timers.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Timers[0].DueTime);
        Assert.False(clock.Timers[0].Fired);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task ActiveIndexKeepsResultAndRunsFinallyBeforeWriteback()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                ActiveResultScript, output.Add, cancellation.Token));
        Assert.Equal(["inner=1|5", "7,101|109|2"], output);
        Assert.Equal(1, loader.StopCalls);
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveIndexCopiesVarArgumentsAfterFinally(
        bool array, bool stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            ReferenceResultScript(array, stop), output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            await run;
        }
        string argument = array ? "97,29|5,5" : "97|5";
        string before = stop ? "71|5|1|5" : "83|5|0|5";
        string snapshot = (stop ? "7,101" : "101,8")
            + "|109|2|" + argument;
        string[] expected = stop
            ? ["argument-before=" + before, "argument-after=97|5", snapshot]
            : ["argument-before=" + before, "argument-after=97|5", "42", snapshot];
        Assert.Equal(expected, output);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.Equal(1, loader.PairCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReferenceIndexFinallyDoesNotCopyArguments(bool array)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        string source = ReferenceResultScript(array, true).Replace(
            "PRINT \"argument-after=\"", "PRINT 1/0\n            PRINT \"argument-after=\"",
            StringComparison.Ordinal);
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateInterpreter(loader, clock).RunAsync(
                    source, output.Add, cancellation.Token));
        string argument = array ? "23,29|5,5" : "23|5";
        Assert.Equal(["argument-before=71|5|1|5", "7,8|19|2|" + argument], output);
        AggregateException cleanup =
            Assert.IsType<AggregateException>(error.Data["UWSCSharp.FinallyErrors"]);
        Assert.IsType<ScriptException>(Assert.Single(cleanup.InnerExceptions));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    public async Task RoutineCopybackFailureCompletesNativeWriteback(
        int kind, bool stop, bool handler)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            DeferredCopybackScript(kind, stop, handler), output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else if (handler)
        {
            await run;
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => run);
            Assert.Contains(kind == 0 ? "範囲外" : "数値", error.Message);
        }
        string snapshot = (stop ? "7,101" : "101,8")
            + "|109|2|97|29,31|101";
        string[] expected = stop ? ["inner=1", snapshot]
            : handler ? ["inner=0", "caught", "recovered=101,109", snapshot]
            : ["inner=0", snapshot];
        Assert.Equal(expected, output);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.Equal(!stop && handler ? 2 : 1, loader.PairCalls);
        Assert.Equal(stop || handler ? 2 : 1, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(true, false, "7,101,9,10", 2)]
    [InlineData(false, false, "7,8,101,10", 2)]
    [InlineData(true, true, "7,8,101,10", 1)]
    [InlineData(false, true, "7,8,9,101", 2)]
    public async Task FailedCopybackUsesZeroOnlyForFailedCoordinate(
        bool failsInFirstCoordinate, bool stop, string expectedArray,
        int coordinateReads)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        const string choose = "Choose(argument,otherValues[cursor],lastArgument)";
        string index = failsInFirstCoordinate
            ? choose + "][OtherCoordinate()" : "OtherCoordinate()][" + choose;
        string source = DeferredCopybackScript(0, stop, handler: false)
            .Replace("PUBLIC reads=0,cursor=0",
                "PUBLIC reads=0,cursor=0,coordinateReads=0", StringComparison.Ordinal)
            .Replace("DIM values[1]=7,8",
                "DIM values[1][1]=7,8,9,10", StringComparison.Ordinal)
            .Replace("values[" + choose + "]",
                "values[" + index + "]", StringComparison.Ordinal)
            .Replace("values[0]+\",\"+values[1]",
                "values[0][0]+\",\"+values[0][1]+\",\"+values[1][0]+\",\"+values[1][1]",
                StringComparison.Ordinal)
            .Replace("+\"|\"+lastArgument", "+\"|\"+lastArgument+\"|\"+coordinateReads",
                StringComparison.Ordinal)
            + """
              FUNCTION OtherCoordinate()
                  coordinateReads=coordinateReads+1
                  RESULT=0
                  IF coordinateReads>1 THEN RESULT=1
              FEND
              """;

        Task run = CreateInterpreter(loader, clock).RunAsync(
            source, output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(
                () => run);
            Assert.Contains("範囲外", error.Message);
        }
        Assert.Equal([stop ? "inner=1" : "inner=0",
            $"{expectedArray}|109|2|97|29,31|101|{coordinateReads}"], output);
        Assert.Equal(1, loader.PairCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MultipleCopybackFailuresKeepFirstErrorAndLaterWrites(
        bool numericFirst, bool stop)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        Task run = CreateInterpreter(loader, clock).RunAsync(
            MultipleCopybackFailureScript(numericFirst, stop),
            output.Add, cancellation.Token);
        if (stop)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => run);
            Assert.Contains(numericFirst ? "数値" : "範囲外", error.Message);
        }
        string snapshot = (stop ? "7,101" : "101,8")
            + "|109|2|97|29,31|101|43,47";
        Assert.Equal([stop ? "inner=1" : "inner=0", snapshot], output);
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(stop ? 1 : 0, loader.StopCalls);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    private static string MultipleCopybackFailureScript(bool numericFirst,
        bool stop)
    {
        string extraCursor = numericFirst ? "9" : "\"bad\"";
        return DeferredCopybackScript(numericFirst ? 1 : 0, stop, handler: false)
            .Replace("PUBLIC reads=0,cursor=0",
                "PUBLIC reads=0,cursor=0,extraCursor=0", StringComparison.Ordinal)
            .Replace("DIM otherValues[1]=29,31",
                "DIM otherValues[1]=29,31\nDIM extraValues[1]=43,47",
                StringComparison.Ordinal)
            .Replace("otherValues[cursor],lastArgument",
                "otherValues[cursor],extraValues[extraCursor],lastArgument", StringComparison.Ordinal)
            .Replace("VAR secondValue,VAR thirdValue",
                "VAR secondValue,VAR extraValue,VAR thirdValue", StringComparison.Ordinal)
            .Replace("thirdValue=101", "thirdValue=101\nextraValue=107\nextraCursor=" + extraCursor,
                StringComparison.Ordinal)
            .Replace("+lastArgument\n", "+lastArgument+\"|\"+extraValues[0]+\",\"+extraValues[1]\n",
                StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveIndexFinallySharesWritebackDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock,
            expireOnTick: true, secondInput: 7);
        var output = new List<string>();
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateInterpreter(loader, clock).RunAsync(
                    ActiveFinallyBudgetScript, output.Add, cancellation.Token));
        Assert.Equal(["101|7,8|2"], output);
        Assert.Equal(1, loader.TickCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.Equal(2, clock.Timers.Count);
        Assert.True(clock.Timers[0].Fired);
        Assert.IsType<AggregateException>(error.Data["UWSCSharp.FinallyErrors"]);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task FailedActiveIndexFinallyPreservesCancellationAndEarlierWriteback()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock, secondInput: 7);
        var output = new List<string>();
        string source = ActiveFinallyBudgetScript.Replace(
            "WHILE TRUE", "WHILE 1/0",
            StringComparison.Ordinal);
        Assert.NotEqual(ActiveFinallyBudgetScript, source);
        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateInterpreter(loader, clock).RunAsync(
                    source, output.Add, cancellation.Token));
        Assert.Equal(["101|7,8|2"], output);
        AggregateException cleanup =
            Assert.IsType<AggregateException>(error.Data["UWSCSharp.FinallyErrors"]);
        Assert.IsType<ScriptException>(Assert.Single(cleanup.InnerExceptions));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task NestedWritebackFinishesBeforeActiveRoutineStops()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock, cancelInNested: true);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                NestedWritebackScript(), output.Add, cancellation.Token));
        Assert.Equal(["inner=1|5|9|3", "7,101|109|2"], output);
        Assert.Equal(1, loader.NestedCalls);
        Assert.Equal(0, loader.StopCalls);
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task ExpiredWritebackDoesNotGiveRoutineFinallyANewDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock,
            expireOnTick: true, cancelOnTick: true);
        var output = new List<string>();
        string source = ActiveResultScript.Replace(
            "\n            RequestStop()\n", "\n            Tick()\n",
            StringComparison.Ordinal);
        Assert.NotEqual(ActiveResultScript, source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                source, output.Add, cancellation.Token));
        Assert.Equal(["7,8|19|2"], output);
        Assert.Equal(1, loader.TickCalls);
        Assert.Equal(0, loader.StopCalls);
        Assert.Equal(2, clock.Timers.Count);
        Assert.True(clock.Timers[0].Fired);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task WritebackInsideFinallyKeepsExistingDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                FinallyBudgetScript, output.Add, cancellation.Token));
        Assert.Equal(["101,109"], output);
        Assert.Equal(1, loader.PairCalls);
        Assert.Equal(1, loader.StopCalls);
        Timer timer = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.DueTime);
        Assert.True(timer.Disposed);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task CancelledArgumentDoesNotEnterOuterNativeCall()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                CancelledArgumentScript, output.Add, cancellation.Token));
        Assert.Equal(["7,5"], output);
        Assert.Equal(0, loader.PairCalls);
        Assert.Equal(1, loader.StopCalls);
        Assert.Single(clock.Timers);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task FailedIndexKeepsEarlierWritebackAndRestoresExecutionState()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock, secondInput: 7,
            cancelOnTick: true);
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader, clock).RunAsync(
                PartialErrorScript, output.Add, cancellation.Token));
        Assert.Equal(["caught", "101|7,8|2", "42", "after=101,109"], output);
        Assert.Equal(2, loader.PairCalls);
        Assert.Equal(1, loader.TickCalls);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer =>
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.DueTime);
            Assert.True(timer.Disposed);
        });
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutOfRangeArrayWritebackSkipsOnlyInvalidArgument(bool reference)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        await CreateInterpreter(loader, clock).RunAsync(
            OutOfRangeArrayScript(reference), output.Add, cancellation.Token);
        Assert.Equal(["42", "7,8|109|2|5,5,3"], output);
        Assert.Equal(1, loader.PairCalls);
        Assert.False(cancellation.IsCancellationRequested);
        Timer timer = Assert.Single(clock.Timers);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.DueTime);
        Assert.True(timer.Disposed);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnregisteredCallCompletesWriteback(bool stop,
        bool computedPath)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        using var loader = new Loader(cancellation, clock);
        var output = new List<string>();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("uwsc-copyback-call-");
        try
        {
            string mainPath = Path.Combine(directory.FullName, "main.uws");
            const string original = "RequestStop()\n            PRINT \"unexpected\"";
            string call = computedPath
                ? """EVAL("CALL <#DBL>bridge <#DBL>+<#DBL>日本語.uws<#DBL>")"""
                : """EVAL("CALL bridge.uws")""";
            string bridgeName = computedPath ? "bridge 日本語.uws" : "bridge.uws";
            Assert.Contains(original, ActiveResultScript);
            File.WriteAllText(mainPath, ActiveResultScript.Replace(original,
                call, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(directory.FullName, bridgeName), $$"""
                TRY
                    PRINT "imported"
                    {{(stop ? "RequestStop()" : "")}}
                FINALLY
                    PRINT "bridge-finally"
                ENDTRY
                """);
            ScriptProgram program = ScriptCompiler.CompileFile(mainPath);
            Task run = CreateInterpreter(loader, clock).RunAsync(
                program, new CallbackScriptHost(output.Add), cancellation.Token);
            if (stop)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                Assert.Equal(["imported", "bridge-finally", "inner=1|5", "7,101|109|2"], output);
            }
            else
            {
                await run;
                Assert.Equal(["imported", "bridge-finally", "inner=0|5", "42", "101,8|109|2"],
                    output);
            }
            Assert.Equal(1, loader.PairCalls);
            Assert.Equal(stop ? 1 : 0, loader.StopCalls);
            Assert.Equal(1, loader.Frees);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string OutOfRangeArrayScript(bool reference) => $$"""
        OPTION OPTFINALLY
        DEF_DLL Pair({{(reference ? "VAR " : "")}}int[],VAR int):int:kernel32.dll
        PUBLIC reads=0
        DIM values[1]=7,8
        DIM number=19
        TRY
            TRY
                PRINT Pair(values[Choose()],number)
            EXCEPT
                PRINT "caught"
            ENDTRY
        FINALLY
            PRINT values[0]+","+values[1]+"|"+number+"|"+reads+"|"+VARTYPE(values[0])+","+VARTYPE(values[1])+","+VARTYPE(number)
        ENDTRY
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                RESULT=99
            ELSE
                RESULT=0
            ENDIF
        FEND
        """ + "\n";

    private static Interpreter CreateInterpreter(Loader loader, Clock clock) =>
        new(windows: new WindowsAutomation(_ =>
            throw new InvalidOperationException("この検査では入力を操作しません。"))
        {
            NativeDllLoader = loader
        }, timeProvider: clock);

    private static string IndexedScript(int mode)
    {
        string nested = mode == 1 ? "RequestStop()\nRESULT=1\n"
            : mode == 2 ? "DIM local=3\nNested(local)\nRESULT=local-8\n"
            : "RESULT=1\n";
        return Declarations + """
            PUBLIC reads=0
            DIM values[1]=7,8
            DIM number=19
            TRY
                PRINT "result="+Pair(values[Choose()],number)
            FINALLY
                PRINT values[0]+","+values[1]+"|"+number+"|"+reads+"|"+VARTYPE(values[0])+","+VARTYPE(values[1])+","+VARTYPE(number)
            ENDTRY
            FUNCTION Choose()
                reads=reads+1
                IFB reads=2
            """ + "\n" + nested + "ELSE\nRESULT=0\nENDIF\nFEND\n";
    }

    private const string BoundedScript = Declarations + """
        PUBLIC reads=0
        DIM first=7,values[1]=7,8
        TRY
            PRINT Pair(first,values[Choose()])
        FINALLY
            PRINT first+"|"+values[0]+","+values[1]+"|"+reads
        ENDTRY
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                RequestStop()
                WHILE TRUE
                    Tick()
                WEND
            ENDIF
            RESULT=0
        FEND
        """ + "\n";

    private const string ActiveResultScript = Declarations + """
        PUBLIC reads=0
        DIM values[1]=7,8
        DIM number=19
        TRY
            PRINT Pair(values[Choose()],number)
        FINALLY
            PRINT values[0]+","+values[1]+"|"+number+"|"+reads
        ENDTRY
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                RESULT=1
                TRY
                    RequestStop()
                    PRINT "unexpected"
                    RESULT=0
                FINALLY
                    PRINT "inner="+RESULT+"|"+VARTYPE(RESULT)
                ENDTRY
            ELSE
                RESULT=0
            ENDIF
        FEND
        """ + "\n";

    private static string DeferredCopybackScript(int kind, bool stop, bool handler)
    {
        string invoke = "PRINT Pair(values[Choose(argument,otherValues[cursor],lastArgument)],number)";
        string body = handler
            ? "TRY\n    " + invoke + "\nEXCEPT\n"
                + "    PRINT \"caught\"\n"
                + "    DIM recoveredFirst=7,recoveredNumber=19\n"
                + "    DIM recovered=Pair(recoveredFirst,recoveredNumber)\n"
                + "    PRINT \"recovered=\"+recoveredFirst+\",\"+recoveredNumber\nENDTRY"
            : invoke;
        string cursor = kind == 0 ? "9" : "\"bad\"";
        return Declarations + $$"""
            PUBLIC reads=0,cursor=0
            DIM values[1]=7,8
            DIM number=19,argument=23,lastArgument=37
            DIM otherValues[1]=29,31
            TRY
            {{body}}
            FINALLY
                PRINT values[0]+","+values[1]+"|"+number+"|"+reads+"|"+argument+"|"+otherValues[0]+","+otherValues[1]+"|"+lastArgument
            ENDTRY
            FUNCTION Choose(VAR firstValue,VAR secondValue,VAR thirdValue)
                reads=reads+1
                IFB reads=2
                    RESULT=1
                    firstValue=71
                    secondValue=73
                    thirdValue=79
                    TRY
                        {{(stop ? "RequestStop()" : "")}}
                        firstValue=83
                        secondValue=89
                        thirdValue=91
                        RESULT=0
                    FINALLY
                        PRINT "inner="+RESULT
                        firstValue=97
                        secondValue=99
                        thirdValue=101
                        cursor={{cursor}}
                    ENDTRY
                ELSE
                    RESULT=0
                ENDIF
            FEND
            """ + "\n";
    }

    private static string ReferenceResultScript(bool array, bool stop)
    {
        string declaration = array
            ? "DIM argument[1]=23,29" : "DIM argument=23";
        string parameter = array ? "changed[]" : "changed";
        string target = array ? "changed[0]" : "changed";
        string snapshot = array
            ? "argument[0]+\",\"+argument[1]+\"|\"+VARTYPE(argument[0])+\",\"+VARTYPE(argument[1])"
            : "argument+\"|\"+VARTYPE(argument)";
        string cleanup = $$"""
                    PRINT "argument-before="+{{target}}+"|"+VARTYPE({{target}})+"|"+RESULT+"|"+VARTYPE(RESULT)
                    {{target}}=97
                    PRINT "argument-after="+{{target}}+"|"+VARTYPE({{target}})
            """;
        return ActiveResultScript.Replace("DIM number=19",
                declaration + "\nDIM number=19", StringComparison.Ordinal)
            .Replace("Choose()", "Choose(argument)", StringComparison.Ordinal)
            .Replace("FUNCTION Choose(argument)",
                "FUNCTION Choose(VAR " + parameter + ")", StringComparison.Ordinal)
            .Replace("        RESULT=1", "        RESULT=1\n        " + target + "=71",
                StringComparison.Ordinal)
            .Replace("            RequestStop()", stop ? "            RequestStop()" : "",
                StringComparison.Ordinal)
            .Replace("            PRINT \"unexpected\"", "            " + target + "=83",
                StringComparison.Ordinal)
            .Replace("            PRINT \"inner=\"+RESULT+\"|\"+VARTYPE(RESULT)",
                cleanup, StringComparison.Ordinal)
            .Replace("+reads\n", "+reads+\"|\"+" + snapshot + "\n",
                StringComparison.Ordinal);
    }

    private static string NestedWritebackScript() => ActiveResultScript.Replace(
        "RESULT=1\n        TRY", "RESULT=1\n        DIM local=3\n        TRY",
        StringComparison.Ordinal).Replace(
            "\n            RequestStop()\n", "\n            Nested(local)\n",
            StringComparison.Ordinal).Replace(
                "PRINT \"inner=\"+RESULT+\"|\"+VARTYPE(RESULT)",
                "PRINT \"inner=\"+RESULT+\"|\"+VARTYPE(RESULT)+\"|\"+local+\"|\"+VARTYPE(local)",
                StringComparison.Ordinal);

    private const string ActiveFinallyBudgetScript = Declarations + """
        PUBLIC reads=0
        DIM first=7,values[1]=7,8
        TRY
            PRINT Pair(first,values[Choose()])
        FINALLY
            PRINT first+"|"+values[0]+","+values[1]+"|"+reads
        ENDTRY
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                TRY
                    RequestStop()
                FINALLY
                    WHILE TRUE
                        Tick()
                    WEND
                ENDTRY
            ENDIF
            RESULT=0
        FEND
        """ + "\n";

    private const string FinallyBudgetScript = Declarations + """
        DIM first=7,second=19
        TRY
            RequestStop()
        FINALLY
            Pair(first,second)
            PRINT first+","+second
        ENDTRY
        """ + "\n";

    private const string CancelledArgumentScript = Declarations + """
        DIM first=7
        TRY
            Pair(first,StopArgument())
        FINALLY
            PRINT first+","+VARTYPE(first)
        ENDTRY
        FUNCTION StopArgument()
            RequestStop()
            RESULT=19
        FEND
        """ + "\n";

    private const string PartialErrorScript = Declarations + """
        PUBLIC reads=0
        DIM first=7,values[1]=7,8
        TRY
            TRY
                PRINT Pair(first,values[Choose()])
            EXCEPT
                PRINT "caught"
            ENDTRY
        FINALLY
            PRINT first+"|"+values[0]+","+values[1]+"|"+reads
        ENDTRY
        DIM nextFirst=7,nextSecond=19
        PRINT Pair(nextFirst,nextSecond)
        PRINT "after="+nextFirst+","+nextSecond
        PRINT Tick()
        FUNCTION Choose()
            reads=reads+1
            IFB reads=2
                RESULT=1/0
            ELSE
                RESULT=0
            ENDIF
        FEND
        """ + "\n";

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly Dictionary<string, Delegate> exports;
        internal int PairCalls;
        internal int StopCalls;
        internal int NestedCalls;
        internal int TickCalls;
        internal int ConsumeCalls;
        internal int Frees;

        internal Loader(CancellationTokenSource cancellation, Clock clock,
            bool cancelInPair = false, bool expireOnTick = false,
            int secondInput = 19, bool cancelOnTick = false,
            bool cancelInNested = false)
        {
            exports = new()
            {
                ["Pair"] = new PairCall((first, second) =>
                {
                    PairCalls++;
                    Assert.Equal(7, Marshal.ReadInt32(first));
                    Assert.Equal(PairCalls == 1 ? secondInput : 19,
                        Marshal.ReadInt32(second));
                    Assert.NotEqual(first, second);
                    Marshal.WriteInt32(first, 101);
                    Marshal.WriteInt32(second, 109);
                    if (cancelInPair)
                    {
                        cancellation.Cancel();
                    }
                    return 42;
                }),
                ["RequestStop"] = new IntCall(() =>
                {
                    StopCalls++;
                    cancellation.Cancel();
                    return 1;
                }),
                ["Nested"] = new ReferenceCall(pointer =>
                {
                    NestedCalls++;
                    Assert.Equal(3, Marshal.ReadInt32(pointer));
                    Marshal.WriteInt32(pointer, 9);
                    if (cancelInNested)
                    {
                        cancellation.Cancel();
                    }
                    return 7;
                }),
                ["Consume"] = new ArgumentCall(value =>
                {
                    ConsumeCalls++;
                    return value;
                }),
                ["ConsumeArray"] = new ReferenceCall(_ =>
                {
                    ConsumeCalls++;
                    return 0;
                }),
                ["Tick"] = new IntCall(() =>
                {
                    TickCalls++;
                    if (cancelOnTick)
                    {
                        cancellation.Cancel();
                    }
                    if (expireOnTick)
                    {
                        clock.Timers[0].Fire();
                    }
                    return 42;
                })
            };
        }

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) =>
            Marshal.GetFunctionPointerForDelegate(exports[export]);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(exports);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int PairCall(nint first, nint second);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ReferenceCall(nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int IntCall();

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ArgumentCall(int value);
    }

    private sealed class Clock : TimeProvider
    {
        internal List<Timer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new Timer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class Timer(TimerCallback callback, object? state,
        TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime = dueTime;
        internal bool Disposed;
        internal bool Fired;

        internal void Fire()
        {
            Assert.False(Disposed);
            Fired = true;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            DueTime = dueTime;
            return !Disposed;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
