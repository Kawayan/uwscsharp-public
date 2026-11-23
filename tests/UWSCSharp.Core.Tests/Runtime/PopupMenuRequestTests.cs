using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PopupMenuRequestTests
{
    [Fact]
    public void HierarchyPreservesOriginalIndicesAndSkipsEmptyItems()
    {
        PopupMenuRequest request = Read("aaa", "", "ccc", "{111", "",
            "222", "333}", "-", "---", "ddd");
        Assert.Equal([0, 2, 7, 8, 9], request.Items.Select(item => item.Index));
        Assert.Equal([3, 5, 6], request.Items[1].Children.Select(item => item.Index));
        Assert.Equal([0, 1, 5, 6, 7], request.Items.Select(item => item.SelectionIndex));
        Assert.Equal([2, 3, 4], request.Items[1].Children.Select(item => item.SelectionIndex));
        Assert.True(request.Items[2].Separator);
        Assert.False(request.Items[3].Separator);
    }

    [Fact]
    public void NestedMenusAndInteriorBracesRemainDistinct()
    {
        PopupMenuRequest request = Read("root", "{child", "{leaf}", "last}", "A{B", "C}D");
        Assert.Equal("leaf", request.Items[0].Children[0].Children[0].Text);
        Assert.Equal("last", request.Items[0].Children[1].Text);
        Assert.Equal("A{B", request.Items[1].Text);
        Assert.Equal("C}D", request.Items[2].Text);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{child")]
    [InlineData("child}")]
    [InlineData("A\0B")]
    public void InvalidHierarchiesAndNulAreRejectedBeforeShowingAnything(string item)
        => Assert.Throws<InvalidOperationException>(() => Read("A", item));

    [Theory]
    [InlineData(-4)]
    [InlineData(5)]
    public void NonZeroBoundsPreserveSourceIndicesButSelectionStartsAtZero(
        int lower)
    {
        var array = UwscArray.CreateBounded([lower], [lower + 2],
            [Value.String("A"), Value.String(""), Value.String("B")]);
        var request = PopupMenuRequest.Read([Value.FromArray(array), Value.Numeric(-20.9), Value.Numeric(90.2)]);
        Assert.Equal([lower, lower + 2], request.Items.Select(item => item.Index));
        Assert.Equal([0, 1], request.Items.Select(item => item.SelectionIndex));
        Assert.Equal(-20, request.X);
        Assert.Equal(90, request.Y);
        request = PopupMenuRequest.Read([Value.FromArray(array), new(ValueKind.Omitted), Value.Numeric(90)]);
        Assert.Null(request.X);
        Assert.Null(request.Y);
    }

    [Fact]
    public void AcceptsMaximumItemCountAndSubmenuDepth()
    {
        PopupMenuRequest wide = Read([.. Enumerable.Repeat("item", 4096)]);
        Assert.Equal(4096, wide.Items.Count);
        Assert.Equal(4095, wide.Items[^1].Index);

        PopupMenuRequest deep = Read(["root",
            .. Enumerable.Repeat("{branch", 15),
            "{leaf}", .. Enumerable.Repeat("last}", 15)]);
        PopupMenuItem item = Assert.Single(deep.Items);
        for (int depth = 0; depth < 16; depth++)
        {
            item = item.Children[0];
        }

        Assert.Equal("leaf", item.Text);
        Assert.Equal(16, item.Index);
        Assert.Empty(item.Children);
    }

    [Fact]
    public void RankItemAndDepthLimitsAreValidated()
    {
        Assert.Throws<InvalidOperationException>(() => PopupMenuRequest.Read([Value.String("A")]));
        Assert.Throws<InvalidOperationException>(() => PopupMenuRequest.Read([
            Value.FromArray(UwscArray.Create([0, 0], [Value.String("A")]))]));
        Assert.Throws<InvalidOperationException>(() => PopupMenuRequest.Read([
            Value.FromArray(UwscArray.Create([4096], Enumerable.Repeat(Value.String("A"), 4097)))]));
        Assert.Throws<InvalidOperationException>(() => Read(["A", .. Enumerable.Repeat("{A", 17)]));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void CanceledRequestStopsBeforeReadingArguments()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        OperationCanceledException error =
            Assert.Throws<OperationCanceledException>(
                () => PopupMenuRequest.Read(null!, source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ScriptEvaluatesArrayOnceAndRejectsArityBeforeSideEffects()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PRINT POPUPMENU(MakeItems(),,90)
            TRY
                POPUPMENU(MakeItems(),0,0,MakeItems())
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION MakeItems()
                PRINT "once"
                DIM items[1]="A","B"
                RESULT=items
            FEND
            """, output.Add);
        Assert.Equal(["once", "1", "caught"], output);
        Assert.Null(adapter.Request!.X);
        Assert.Equal(2, adapter.Request.Items.Count);
    }

    private static PopupMenuRequest Read(params string[] items) => PopupMenuRequest.Read([
        Value.FromArray(UwscArray.Create([items.Length - 1], items.Select(Value.String)))]);

    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal PopupMenuRequest? Request;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> values,
            CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.PopupMenu, name);
            Request = PopupMenuRequest.Read(values);
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
