using System.Collections;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ComValidationTests
{
    private sealed class Runtime : IComRuntime
    {
        internal readonly object Root = new();
        internal readonly List<object> Released = [];
        internal Func<object?[], object?>? Operation;
        internal int Invocations;

        public object Create(string name) => Root;

        public object GetActive(string name) => Root;

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign)
        {
            Invocations++;
            return Operation?.Invoke(arguments);
        }

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign, IReadOnlyList<bool> byReference) =>
            Invoke(target, name, arguments, assign);

        public void Release(object target) => Released.Add(target);
    }

    private sealed class BrokenValues(Exception failure) : IReadOnlyList<object?>
    {
        public int Count => 1;

        public object? this[int index] => throw failure;

        public IEnumerator<object?> GetEnumerator() => throw failure;

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("argument")]
    [InlineData("depth")]
    [InlineData("names")]
    public async Task DirectInputValidationKeepsTypeAndSkipsRuntime(string condition)
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        await using var other = new ComSession(new Runtime());
        Value target = await session.CreateAsync("fixture", false, default);
        Value foreign = await other.CreateAsync("fixture", false, default);
        Value argument = foreign;
        if (condition == "depth")
        {
            argument = Value.Numeric(1);
            for (int i = 0; i < 130; i++)
            {
                argument = Value.FromArray(UwscArray.Create([0], [argument]));
            }
        }

        InvalidOperationException failure;
        if (condition == "names")
        {
            failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeWithReferencesAsync(target, "Change",
                    [new(Value.Numeric(1)) { Name = "A" },
                    new(Value.Numeric(2)) { Name = "a" }], null, default));
        }
        else
        {
            failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(condition == "target" ? foreign : target,
                    "Change", [argument], null, default));
        }

        Assert.Equal(failure.Message, session.GetScriptErrorMessage(failure));
        Assert.Null(other.GetScriptErrorMessage(failure));
        Assert.Equal(0, runtime.Invocations);
    }

    [Theory]
    [InlineData("PRINT GETOLEITEM(o)")]
    [InlineData("PRINT LENGTH(o)")]
    [InlineData("FOR x IN o\nPRINT x\nNEXT")]
    public async Task NonEnumerableObjectCanBeHandled(string call)
    {
        var runtime = new Runtime();
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: runtime)).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nTRY\n" + call
                + "\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\nPRINT \"after\"",
                output.Add);
        Assert.Equal(["3", "after"], output);
        Assert.Equal([runtime.Root], runtime.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputAggregateRequiresEveryFailureToBeKnown(bool mixed)
    {
        var unknown = new InvalidOperationException("内部取得障害");
        object child = new();
        object returned = new();
        var runtime = new Runtime
        {
            Operation = arguments =>
            {
                arguments[0] = double.NaN;
                arguments[1] = mixed
                    ? new ComArrayValue(12, [0], [0], new BrokenValues(unknown))
                    : double.PositiveInfinity;
                arguments[2] = child;
                return returned;
            }
        };
        var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(async () =>
            await session.InvokeWithReferencesAsync(target, "Change",
                [new(Value.Numeric(1), true), new(Value.Numeric(2), true),
                new(Value.Nothing, true)], null, default));
        Assert.Equal(mixed ? null : failure.Message,
            session.GetScriptErrorMessage(failure));
        Assert.Equal(2, failure.InnerExceptions.Count);
        if (mixed)
        {
            Assert.Contains(unknown, failure.InnerExceptions);
        }

        await session.DisposeAsync();
        Assert.Equal([child, returned, runtime.Root], runtime.Released);
    }

    [Fact]
    public async Task DeepReturnedArrayIsAClassifiedValidationFailure()
    {
        object returned = 1d;
        for (int i = 0; i < 130; i++)
        {
            returned = new object?[] { returned };
        }

        var runtime = new Runtime { Operation = _ => returned };
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(target, "Change", [], null, default));
        Assert.Contains("入れ子", failure.Message);
        Assert.Equal(failure.Message, session.GetScriptErrorMessage(failure));
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("capacity")]
    [InlineData("elements")]
    public async Task InvalidReturnedArrayShapeIsClassified(string condition)
    {
        ComArrayValue returned = condition switch
        {
            "rank" => new(12, [0, 0], [0], []),
            "capacity" => new(12, [0], [UwscArray.MaximumElements], []),
            _ => new(12, [0], [0], [1d, 2d])
        };
        var runtime = new Runtime { Operation = _ => returned };
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(target, "Change", [], null, default));
        Assert.Equal(failure.Message, session.GetScriptErrorMessage(failure));
    }
}
