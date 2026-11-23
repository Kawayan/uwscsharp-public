using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionWordEvaluationTests
{
    [Theory]
    [InlineData(false, "\t")]
    [InlineData(true, "<#TAB>")]
    public async Task InternalArrayAndScalarKeepDifferentTextRules(bool preserve, string expected)
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.Create([2],
            [Value.String("<#TAB>"), Value.String("<#_CR>"), Value.String("重複")]));
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new LiteralExpr(Value.String("<#TAB>")), new NameExpr("a"),
                new LiteralExpr(Value.String("重複"))], context, Format, preserve);
        Assert.Equal(["<#TAB>", expected, "\r\n", "重複", "重複"], words);
    }

    [Fact]
    public async Task PartialInternalArrayUsesRowMajorRegionAndEvaluatesIndexOnce()
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.Create([1, 1, 1],
            Enumerable.Range(0, 8).Select(value => Value.String(value + "<#_TAB>"))));
        int calls = 0;
        context.Call = async () =>
        {
            await Task.Yield();
            calls++;
            return Value.Numeric(1);
        };
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new IndexExpr("a", [new CallExpr("index", [])])], context, Format, false);
        Assert.Equal(["4\t", "5\t", "6\t", "7\t"], words);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariantArrayUsesBoundsAndDoesNotExpandSpecialCharacters(bool row)
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.CreateBounded([3, -2], [4, -1],
            [Value.String("一"), Value.String("二"), Value.String("<#_TAB>"), Value.String("四")])
            .WithNativeElementType(12));
        Expr expression = row ? new IndexExpr("a", [new LiteralExpr(Value.Numeric(4))])
            : new NameExpr("a");
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [expression], context, Format, false);
        Assert.Equal(row ? ["<#_TAB>", "四"] : new[] { "一", "二", "<#_TAB>", "四" }, words);
    }

    [Fact]
    public async Task ScalarIndexAndFunctionArrayDoNotUseInternalArrayConversion()
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.Create([0], [Value.String("<#_TAB>")]));
        context.Call = () => Task.FromResult(context.Values["a"]);
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new IndexExpr("a", [new LiteralExpr(Value.Numeric(0))]), new CallExpr("array", [])],
            context, Format, false);
        Assert.Equal(["<#_TAB>", "<#_TAB>"], words);
    }

    [Fact]
    public async Task NestedAndTypedArraysReachFormatterWithoutRecursiveExpansion()
    {
        var context = new Context();
        var nested = Value.FromArray(UwscArray.Create([0], [Value.String("内側")]));
        context.Values["a"] = Value.FromArray(UwscArray.Create([0], [nested]));
        var typed = Value.FromArray(UwscArray.Create([0], [Value.Numeric(42)]).WithNativeElementType(3));
        context.Values["typed"] = typed;
        List<Value> observed = [];
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new NameExpr("a"), new NameExpr("typed")], context, (value, token) =>
            {
                observed.Add(value);
                return ValueTask.FromResult("変換");
            }, false);
        Assert.Equal([nested, typed], observed);
        Assert.Equal(["変換", "変換"], words);
    }

    [Fact]
    public async Task FormattingIsOrderedAndFailurePreventsFollowingExpression()
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.Create([1], [Value.Numeric(1), Value.Numeric(2)]));
        int calls = 0;
        context.Call = () =>
        {
            calls++;
            return Task.FromResult(Value.Numeric(3));
        };
        var failure = new InvalidOperationException("文字列化失敗");
        List<Value> observed = [];
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpeechRecognitionWordEvaluation.EvaluateAsync(
                [new NameExpr("a"), new CallExpr("later", [])], context, async (value, token) =>
                {
                    await Task.Yield();
                    observed.Add(value);
                    if (value.Number == 2)
                    {
                        throw failure;
                    }

                    return "一";
                }, false)));
        Assert.Equal([Value.Numeric(1), Value.Numeric(2)], observed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationAfterFormatterReturnsPreventsFurtherEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Context();
        context.Call = () => throw new InvalidOperationException("後続式を評価した");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SpeechRecognitionWordEvaluation.EvaluateAsync(
                [new LiteralExpr(Value.String("一")), new CallExpr("later", [])], context,
                (value, token) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult(value.Text);
                }, false, cancellation.Token));
    }

    [Fact]
    public async Task EmptyArrayDoesNotSwitchModesOrSkipFollowingScalar()
    {
        var context = new Context();
        context.Values["empty"] = Value.FromArray(UwscArray.Create([-1]));
        IEnumerable<string> words = await SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new NameExpr("empty"), new LiteralExpr(Value.String(""))], context, Format, false);
        Assert.Equal([""], words);
    }

    [Fact]
    public async Task UnsupportedVariantRankIsExplicitBeforeFormatting()
    {
        var context = new Context();
        context.Values["a"] = Value.FromArray(UwscArray.Create([0, 0, 0]).WithNativeElementType(12));
        await Assert.ThrowsAsync<NotSupportedException>(() => SpeechRecognitionWordEvaluation.EvaluateAsync(
            [new NameExpr("a")], context, (_, _) => throw new InvalidOperationException("変換した"), false));
    }

    private static ValueTask<string> Format(Value value, CancellationToken token) =>
        ValueTask.FromResult(value.Display());

    private sealed class Context : IEvaluationContext
    {
        internal readonly Dictionary<string, Value> Values = new(StringComparer.OrdinalIgnoreCase);
        internal Func<Task<Value>>? Call;

        public Value Read(string name) => Values[name];

        public async ValueTask<Value> InvokeAsync(CallExpr call, CancellationToken token) =>
            await Call!();
    }
}
