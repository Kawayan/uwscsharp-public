using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ArrayTests
{
    [Fact]
    public void BoundedArraysPreserveIndicesAndSnapshotIsolation()
    {
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7],
            Enumerable.Range(1, 6).Select(number => Value.Numeric(number)));
        Assert.Equal(2, source.GetLength(0));
        Assert.Equal(Value.Numeric(6), source.Get([-1, 7]));
        UwscArray row = source.Get([-1]).AsArray();
        Assert.Equal([5], row.LowerBounds);
        Assert.Equal([7], row.UpperBounds);
        UwscArray updated = source.Set([-2, 6], Value.Numeric(99));
        Assert.Equal(Value.Numeric(99), updated.Get([-2, 6]));
        Assert.Equal(Value.Numeric(2), source.Get([-2, 6]));
        Assert.Throws<InvalidOperationException>(() => source.Get([-3, 5]));
        Assert.Throws<InvalidOperationException>(() => source.Set([-1],
            Value.FromArray(UwscArray.FromValues(row.Values))));
    }

    [Fact]
    public void BoundedArrayValidationRejectsOverflowAndAllowsEmptyAxis()
    {
        Assert.Equal(0, UwscArray.CreateBounded([3, 5], [2, 7]).Count);
        Assert.Throws<InvalidOperationException>(() =>
            UwscArray.CreateBounded([int.MinValue], [int.MaxValue]));
        Assert.Throws<InvalidOperationException>(() =>
            UwscArray.CreateBounded([1, 1], [1001, 1001]));
    }

    [Fact]
    public async Task DeclaresUpperBoundsAndInitializesMissingSlots() => Assert.Equal(new[] { "3", "1", "", "b" }, await Run("DIM a[2]=1\nPRINT LENGTH(a)\nPRINT a[0]\nPRINT a[2]\na[1]=\"b\"\nPRINT a[1]"));

    [Fact]
    public async Task InfersEmptyAndNonemptyArraySizes() => Assert.Equal(new[] { "3", "0", "-1" }, await Run("DIM a[]=1,2,3\nDIM b[]\nPRINT LENGTH(a)\nPRINT LENGTH(b)\nPRINT RESIZE(b)"));

    [Theory]
    [InlineData("DIM a[1][2]=1,2,3,4,5,6")]
    [InlineData("DIM a[1,2]=1,2,3,4,5,6")]
    public async Task SupportsRowMajorMultidimensionalArrays(
        string declaration) => Assert.Equal(
        new[] { "2", "3", "6", "9" }, await Run(declaration
        + "\nPRINT LENGTH(a)\nPRINT LENGTH(a[1])\nPRINT a[1][2]\na[0,1]=9\nPRINT a[0][1]"));

    [Fact]
    public async Task SnapshotAssignmentDoesNotAliasMutableStorage() => Assert.Equal(new[] { "1", "9" }, await Run("DIM a[]=1,2\nb=a\nb[0]=9\nPRINT a[0]\nPRINT b[0]"));

    [Fact]
    public async Task PartialArrayAssignmentAndFunctionReturnKeepValueIsolation() => Assert.Equal(new[] { "1,2,7,8", "99,8" }, await Run("DIM a[1][1]=1,2,3,4\na[1]=Make()\nb=a[1]\nb[0]=99\nPRINT JOIN(a,\",\")\nPRINT JOIN(b,\",\")\nFUNCTION Make()\nRESULT=SPLIT(\"7 8\",,FALSE,TRUE)\nFEND"));

    [Fact]
    public async Task ArrayFunctionsUseInclusiveClampedRanges() => Assert.Equal(new[] { "b|c", "b-c", "6", "2" }, await Run("a=SPLIT(\"a,b,c\",\",\")\nPRINT JOIN(a,\"|\",FALSE,1,99)\nb=SLICE(a,1,2)\nPRINT JOIN(b,\"-\")\nDIM n[]=1,2,3\nPRINT CALCARRAY(n,CALC_ADD)\nPRINT CALCARRAY(n,CALC_AVR)"));

    [Fact]
    public async Task SplitNumericConversionAndSkipEmptyAreIndependent() => Assert.Equal(new[] { "2", "1", "2", "x" }, await Run("a=SPLIT(\"1,,x,2\",\",\",TRUE,TRUE)\nPRINT LENGTH(a)\nPRINT a[0]\nPRINT a[1]\nPRINT JOIN(SPLIT(\",x,\",\",\"),\"|\",TRUE)"));

    [Fact]
    public async Task EmptySplitAndEmptySeparatorFollowRecoveredBranches() => Assert.Equal(new[] { "0", "a,b" }, await Run("PRINT LENGTH(SPLIT(\"\"))\nPRINT JOIN(SPLIT(\"a b\",\"\"),\",\")"));

    [Fact]
    public async Task SplitNumericModeUsesItsOwnExponentPolicy() => Assert.Equal(new[] { "1000", "1", "" }, await Run("a=SPLIT(\"1e3,bad\",\",\",FALSE,TRUE)\nPRINT a[0]\nPRINT CHKNUM(\"1e+3\")\nPRINT a[1]"));

    [Fact]
    public async Task SplitNumericModeAcceptsGroupedSampleNumbersOnly()
    {
        Assert.Equal(["1456|-2345.5|1234567", "0", "1,456"], await Run(
            "PRINT JOIN(SPLIT(\"1,456 -2,345.5 1,234,567 12,34 1,,000\","
            + "\" \",TRUE,TRUE),\"|\")\nPRINT CHKNUM(\"1,456\")\n"
            + "PRINT JOIN(SPLIT(\"1,456\",\" \",TRUE,FALSE))"));
    }

    [Fact]
    public async Task SetClearAndShiftUpdateOnlySelectedRow() => Assert.Equal(new[] { "1,2,3,9,9,9", ",1,2,9,9,9", "2,," }, await Run("DIM a[1][2]=1,2,3,4,5,6\nSETCLEAR(a[1],9)\nPRINT JOIN(a,\",\")\nSHIFTARRAY(a[0],1)\nPRINT JOIN(a,\",\")\nSHIFTARRAY(a[0],-2)\nPRINT JOIN(a[0],\",\")"));

    [Fact]
    public async Task ResizeChangesUpperBoundAndPreservesValues() => Assert.Equal(new[] { "4", "5", "1,2,3,,", "1", "1,2" }, await Run("DIM a[]=1,2,3\nPRINT RESIZE(a,4)\nPRINT LENGTH(a)\nPRINT JOIN(a,\",\")\nPRINT RESIZE(a,1)\nPRINT JOIN(a,\",\")"));

    [Fact]
    public async Task ResizePreservesRemainingAxes() => Assert.Equal(new[] { "3", "2", "1,2,3,4,," }, await Run("DIM a[1][1]=1,2,3,4\nRESIZE(a,2)\nPRINT LENGTH(a)\nPRINT LENGTH(a[0])\nPRINT JOIN(a,\",\")"));

    [Fact]
    public async Task ForInUsesCollectedArrayAndClearsCounter() => Assert.Equal(new[] { "1", "2", "3", "" }, await Run("DIM a[]=1,2,3\nFOR x IN a\nPRINT x\nSETCLEAR(a,9)\nNEXT\nPRINT x"));

    [Fact]
    public async Task ValueArrayParameterIsIndependentButVarCopiesBack() => Assert.Equal(new[] { "1", "7" }, await Run("DIM a[]=1,2\nChangeCopy(a)\nPRINT a[0]\nUpdate(a)\nPRINT a[0]\nPROCEDURE ChangeCopy(x[])\nx[0]=9\nFEND\nPROCEDURE Update(VAR x[])\nx[0]=7\nFEND"));

    [Fact]
    public async Task ArrayReferenceCopyoutReevaluatesCallerIndex() => Assert.Equal(new[] { "1", "1" }, await Run("PUBLIC row=0\nDIM a[1][0]=1,2\nUpdate(a[row])\nPRINT a[0,0]\nPRINT a[1,0]\nPROCEDURE Update(VAR x[])\nrow=1\nFEND"));

    [Fact]
    public async Task ScalarArrayCellCanBePassedByReference() => Assert.Equal(new[] { "9,2" }, await Run("DIM a[]=1,2\nSetValue(a[0])\nPRINT JOIN(a,\",\")\nPROCEDURE SetValue(VAR x)\nx=9\nFEND"));

    [Fact]
    public async Task WholeVarArrayMayResizeAtNormalReturn() => Assert.Equal(new[] { "4", "1,2,," }, await Run("DIM a[]=1,2\nGrow(a)\nPRINT LENGTH(a)\nPRINT JOIN(a,\",\")\nPROCEDURE Grow(VAR x[])\nRESIZE(x,3)\nFEND"));

    [Fact]
    public async Task QsortPermutesLinkedArraysAndLeavesTrailingEmpty() => Assert.Equal(new[] { "1,2,3,", "a,b,c,tail" }, await Run("DIM keys[3]=3,1,2\nDIM labels[]=\"c\",\"a\",\"b\",\"tail\"\nQSORT(keys,QSRT_A,labels)\nPRINT JOIN(keys,\",\")\nPRINT JOIN(labels,\",\")"));

    [Fact]
    public async Task QsortDescendingAndEqualKeysUseRecoveredSwapPattern() => Assert.Equal(new[] { "3,2,1", "y,x" }, await Run("DIM a[]=1,3,2\nQSORT(a,QSRT_D)\nPRINT JOIN(a,\",\")\nDIM keys[]=1,1\nDIM labels[]=\"x\",\"y\"\nQSORT(keys,,labels)\nPRINT JOIN(labels,\",\")"));

    [Fact]
    public void EmptyAggregationAndShiftEdgesAreExplicit()
    {
        var empty = UwscArray.FromValues([]);
        Assert.Equal(Value.Numeric(0),
            ArrayOperations.Calculate(empty, 1));
        Assert.Equal(ValueKind.Empty,
            ArrayOperations.Calculate(empty, 4).Kind);
        var array = UwscArray.FromValues([Value.Numeric(
            1), default, Value.Numeric(3)]);
        Assert.Equal(Value.Numeric(2),
            ArrayOperations.Calculate(array, 4));
        Assert.All(ArrayOperations.Shift(array,
            int.MinValue).Values, v => Assert.Equal(ValueKind.Empty,
            v.Kind));
        Assert.All(ArrayOperations.Shift(array,
            int.MaxValue).Values, v => Assert.Equal(ValueKind.Empty,
            v.Kind));
    }

    [Theory]
    [InlineData("DIM a[1]=1,2,3", 1)]
    [InlineData("DIM a[1]\nPRINT a[2]", 2)]
    [InlineData("DIM a[1]\na[-1]=0", 2)]
    [InlineData("DIM a[1000000]", 1)]
    [InlineData("DIM a[1]\nRESIZE(a,-2)", 2)]
    [InlineData("DIM a[1][1]\nRESIZE(a[0],4)", 2)]
    [InlineData("DIM a[1]\nQSORT(a,6)", 2)]
    [InlineData("DIM a[1]\nF(a)\nPROCEDURE F(x[][])\nFEND", 2)]
    [InlineData("DIM a[1]\nTOKEN(\",\",a)", 2)]
    public async Task ArrayErrorsPreserveSourceLine(
        string source, int line) => Assert.Equal(line,
        (await Assert.ThrowsAsync<ScriptException>(() => Run(
        source))).Line);

    [Fact]
    public async Task CancelledArrayRoutineDoesNotCopyBack()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new TestHost
        {
            OnOutput = () => cancellation.Cancel()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync(
            "DIM a[]=1,2\nF(a)\nPROCEDURE F(VAR x[])\nSETCLEAR(x,9)\nPRINT 1\nPRINT 2\nFEND",
            host, cancellation.Token));
        Assert.Equal(new[] { "1" }, host.Output);
    }

    [Fact]
    public async Task ReusedProgramDoesNotShareArrayUpdatesAcrossExecutions()
    {
        ScriptProgram program = ScriptCompiler.Compile(
            "DIM a[]=1,2\na[0]=a[0]+1\nPRINT JOIN(a,\",\")");
        var runner = new Interpreter();
        var first = new TestHost();
        var second = new TestHost();
        await Task.WhenAll(runner.RunAsync(program, first),
            runner.RunAsync(program, second));
        Assert.Equal(new[] { "2,2" }, first.Output);
        Assert.Equal(first.Output, second.Output);
    }
}
