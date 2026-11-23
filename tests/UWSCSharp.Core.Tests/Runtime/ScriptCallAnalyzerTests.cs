using System.Reflection;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptCallAnalyzerTests
{
    [Fact]
    public void ReportsUnresolvedNameAndMissingArgumentSeparatelyFromSyntax()
    {
        const string source = "PRINT NO_SUCH_FUNCTION()\nGETID()";
        Assert.NotNull(ScriptCompiler.CompileWithoutExternalFiles(source));
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(source);
        Assert.Collection(result.Diagnostics,
            item =>
            {
                Assert.Equal(1, item.Line);
                Assert.Equal("unknown-function", item.Code);
                Assert.Equal("NO_SUCH_FUNCTION", item.Function);
            },
            item =>
            {
                Assert.Equal(2, item.Line);
                Assert.Equal("argument-shape", item.Code);
                Assert.Equal("GETID", item.Function);
            });
        Assert.False(result.Truncated);
        Assert.Equal(0, result.UncheckedSyntaxNodes);
    }

    [Theory]
    [InlineData("ABS()")]
    [InlineData("FOPEN()")]
    [InlineData("GETID()")]
    [InlineData("GETID(, \"\")")]
    [InlineData("SLEEP()")]
    [InlineData("GETTIME(1, 2, 3)")]
    [InlineData("RANDOM(1, 2)")]
    [InlineData("TOKEN(, text)")]
    [InlineData("RESIZE()")]
    [InlineData("SETCLEAR()")]
    [InlineData("SHIFTARRAY(values)")]
    [InlineData("QSORT(values, , )")]
    [InlineData("LOGPRINT()")]
    [InlineData("STOPFORM()")]
    [InlineData("EVAL()")]
    [InlineData("CTRLTRD(, 1)")]
    [InlineData("SETHOTKEY()")]
    public void ChecksBuiltinArgumentShapesWithoutEvaluating(string source)
    {
        ScriptCallDiagnostic item = Assert.Single(
            ScriptCallAnalyzer.Analyze(source).Diagnostics);
        Assert.Equal("argument-shape", item.Code);
    }

    [Theory]
    [InlineData("abs(1)")]
    [InlineData("FOPEN(\"not-read\")")]
    [InlineData("getid(\"not-searched\", , 0)")]
    [InlineData("SLEEP(0)")]
    [InlineData("GETTIME(,)")]
    [InlineData("RANDOM()")]
    [InlineData("TOKEN(\",\", text)")]
    [InlineData("RESIZE(values)")]
    [InlineData("SETCLEAR(values)")]
    [InlineData("SHIFTARRAY(values, 1)")]
    [InlineData("QSORT(values, )")]
    [InlineData("LOGPRINT(1, , , , )")]
    [InlineData("STOPFORM(1, , )")]
    [InlineData("EVAL(\"Missing()\")")]
    [InlineData("CTRLTRD(0, 0)")]
    [InlineData("SETHOTKEY(,)")]
    public void PreservesOptionalAndOmittedArguments(string source)
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(source);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.UncheckedSyntaxNodes);
    }

    [Theory]
    [InlineData("Sample(1)", false)]
    [InlineData("Sample(1, )", false)]
    [InlineData("Sample(1, 2)", false)]
    [InlineData("Sample()", true)]
    [InlineData("Sample(, 2)", true)]
    [InlineData("Sample(1, 2, 3)", true)]
    public void ResolvesForwardDefinitionsAndDefaultParameters(
        string call, bool expectedWarning)
    {
        string source = call
            + "\nFUNCTION Sample(first, second = 2)"
            + "\nRESULT = first + second\nFEND";
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(source);
        Assert.Equal(expectedWarning ? 1 : 0, result.Diagnostics.Count);
        if (expectedWarning)
        {
            Assert.Equal("argument-shape", result.Diagnostics[0].Code);
        }
    }

    [Fact]
    public void TraversesRecursionWithoutCallingIt()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            PRINT Sum(1)
            FUNCTION Sum(value)
                RESULT = Sum(value)
            FEND
            """);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ResolvesModuleLocalRoutinesBeforeGlobalRoutines()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            Work(1)
            MODULE Sample
                PROCEDURE Check()
                    Work(1)
                FEND
                PROCEDURE Work(first, second)
                FEND
            ENDMODULE
            PROCEDURE Work(first)
            FEND
            """);
        ScriptCallDiagnostic item = Assert.Single(result.Diagnostics);
        Assert.Equal(4, item.Line);
        Assert.Equal("argument-shape", item.Code);
    }

    [Fact]
    public void ThreadChecksRoutineArgumentsButLeavesMembersUnresolved()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            THREAD Work()
            THREAD obj.Work(Missing())
            PROCEDURE Work(value)
            FEND
            """);
        Assert.Equal(["argument-shape", "unknown-function"],
            result.Diagnostics.Select(item => item.Code));
        Assert.Equal(1, result.UncheckedMemberAccesses);
    }

    [Fact]
    public void IgnoresCommentsAndStringsButCountsDynamicBoundaries()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            // Missing()
            PRINT "Missing()"
            EVAL("CALL missing_dependency")
            SETHOTKEY(1, 0, "UnknownCallback")
            PRINT obj.Value
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.UncheckedDynamicCalls);
        Assert.Equal(1, result.UncheckedMemberAccesses);
    }

    [Fact]
    public void VisitsInitializersNestedCallsAndUncalledRoutines()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            PUBLIC value = ABS()
            DIM items[MissingBound()] = MissingInitial()
            PRINT ABS(MissingNested())
            PROCEDURE Uncalled()
                MissingBody()
            FEND
            """);
        Assert.Equal([1, 2, 2, 3, 5],
            result.Diagnostics.Select(item => item.Line));
        Assert.Equal(["ABS", "MissingBound", "MissingInitial",
            "MissingNested", "MissingBody"],
            result.Diagnostics.Select(item => item.Function));
    }

    [Fact]
    public void NeverRunsHostOrFileOperations()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "uwsc-analysis-" + Guid.NewGuid().ToString("N"));
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
            $"EXEC(\"must-not-start.exe\")\nFOPEN(\"{path}\", F_WRITE)"
            + "\nMSGBOX(\"must-not-open\")\nSLEEP(999)\nPRINT ABS(Missing())");
        Assert.Equal("Missing", Assert.Single(result.Diagnostics).Function);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("CALL missing_dependency", 1)]
    [InlineData("IFB FALSE\nCALL missing_dependency\nENDIF", 2)]
    public void RejectsExternalCallBeforeReading(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCallAnalyzer.Analyze(source));
        Assert.Equal(line, error.Line);
        Assert.Contains("外部CALL", error.Message);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void LimitsDiagnosticsAndReportsTruncation(int count,
        bool truncated)
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
            string.Join('\n', Enumerable.Repeat("Missing()", count)));
        Assert.Equal(100, result.Diagnostics.Count);
        Assert.Equal(truncated, result.Truncated);
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ScriptCallAnalyzer.Analyze("PRINT 1", cancellation.Token));
    }

    [Fact]
    public void RecognizesEveryCurrentlyRegisteredBuiltin()
    {
        int count = 0;
        foreach (FieldInfo field in typeof(BuiltinFunctionNames)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static))
        {
            if (field.GetValue(null) is not string name
                || !BuiltinFunctions.IsBuiltin(name))
            {
                continue;
            }

            ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
                $"PRINT {name}()");
            Assert.DoesNotContain(result.Diagnostics,
                item => item.Code != "argument-shape");
            Assert.Equal(0, result.UncheckedSyntaxNodes);
            count++;
        }

        Assert.True(count > 100);
    }

    [Fact]
    public void TraversesControlFlowWithoutTestingReachability()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            OPTION OPTFINALLY
            IFB MissingIf()
                MissingThen()
            ELSEIF MissingElseIf()
                MissingElse()
            ENDIF
            FOR i = MissingStart() TO MissingEnd() STEP MissingStep()
                MissingFor()
            NEXT
            FOR item IN MissingCollection()
                MissingItem()
            NEXT
            WHILE MissingWhile()
                MissingWhileBody()
            WEND
            REPEAT
                MissingRepeat()
            UNTIL MissingUntil()
            SELECT MissingSelector()
            CASE MissingCase()
                MissingCaseBody()
            DEFAULT
                MissingDefault()
            SELEND
            TRY
                MissingTry()
            FINALLY
                MissingFinally()
            ENDTRY
            WITH MissingTarget()
                .Value = MissingAssigned()
            ENDWITH
            EXITEXIT MissingExit()
            """);
        Assert.Equal(23, result.Diagnostics.Count);
        Assert.All(result.Diagnostics,
            item => Assert.Equal("unknown-function", item.Code));
        Assert.Equal(0, result.UncheckedSyntaxNodes);
        Assert.Equal(1, result.UncheckedMemberAccesses);
    }

    [Fact]
    public void TraversesModuleInitializersAndNestedMemberArguments()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            PRINT obj.Method(MissingArgument())
            MODULE Sample
                DIM field = ABS()
                DIM values[ABS()] = ABS()
                PROCEDURE Read()
                    PRINT obj.Method(MissingNested())
                FEND
            ENDMODULE
            """);
        Assert.Equal(["MissingArgument", "ABS", "ABS", "ABS",
            "MissingNested"],
            result.Diagnostics.Select(item => item.Function));
        Assert.Equal(2, result.UncheckedMemberAccesses);
        Assert.Equal(0, result.UncheckedSyntaxNodes);
    }

    [Fact]
    public void DoesNotAssumeArgumentTypesOrValuesAreValid()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
            "SQRT(-1)\nGETID(FALSE, , -100)\nSHIFTARRAY(1, 2)");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void KeepsUnknownCallsAfterEvalAsUnresolvedWarnings()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze(
            "EVAL(\"CALL dynamic_dependency\")\nDynamicFunction()");
        Assert.Equal(1, result.UncheckedDynamicCalls);
        Assert.Equal("unknown-function",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ThreadDoesNotTreatBuiltinsAsUserRoutines()
    {
        ScriptCallDiagnostic item = Assert.Single(
            ScriptCallAnalyzer.Analyze("THREAD ABS(1)").Diagnostics);
        Assert.Equal("unknown-function", item.Code);
        Assert.Contains("THREAD", item.Message);
    }

    [Fact]
    public void HandlesLongFlatExpressionWithinCompilerTokenLimit()
    {
        string source = "PRINT " + string.Join('+',
            Enumerable.Repeat("1", 2046)) + "+Missing()";
        ScriptCallDiagnostic item = Assert.Single(
            ScriptCallAnalyzer.Analyze(source).Diagnostics);
        Assert.Equal("Missing", item.Function);
    }

    [Fact]
    public void VisitsNamedAndReferenceArgumentsInsideOpaqueMembers()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            PRINT obj.Run(value = MissingNamed())
            PRINT obj.Run(VAR values[MissingReference()])
            """);
        Assert.Equal(["MissingNamed", "MissingReference"],
            result.Diagnostics.Select(item => item.Function));
        Assert.Equal(2, result.UncheckedMemberAccesses);
        Assert.Equal(0, result.UncheckedSyntaxNodes);
    }

    [Fact]
    public void VisitsExceptionHandlerWithoutRaisingAnException()
    {
        ScriptCallAnalysis result = ScriptCallAnalyzer.Analyze("""
            TRY
                PRINT 1
            EXCEPT
                MissingHandler()
            ENDTRY
            """);
        Assert.Equal(4, Assert.Single(result.Diagnostics).Line);
    }
}
