using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class ComReferenceSessionTests
{
    private sealed class Runtime : IComRuntime
    {
        internal readonly object Root = new();
        internal readonly List<object> Released = [];
        internal readonly List<int> Threads = [];
        internal Func<object?[], IReadOnlyList<bool>, object?>? Operation;

        public object Create(string name)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return Root;
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign) =>
            throw new InvalidOperationException("VAR用入口を使ってください。");

        public object? Invoke(object target, string name, object?[] arguments, bool assign,
            IReadOnlyList<bool> byReference)
        {
            Assert.Same(Root, target);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Threads.Add(Environment.CurrentManagedThreadId);
            return Operation!(arguments, byReference);
        }

        public void Release(object target)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Released.Add(target);
        }
    }

    [Fact]
    public async Task ModifiersAndSetterValueRemainSeparateOnSta()
    {
        var runtime = new Runtime();
        object returned = new();
        runtime.Operation = (arguments, byReference) =>
        {
            Assert.Equal([true, false], byReference);
            Assert.Equal("input", Assert.IsType<VariantWrapper>(arguments[0]).WrappedObject);
            Assert.Equal(7d, arguments[1]);
            arguments[0] = 42d;
            return returned;
        };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        WindowsMemberResult result = await session.InvokeWithReferencesAsync(root, "Property",
            [new(Value.String("input"), true)], Value.Numeric(7), default);
        Assert.Equal([Value.Numeric(42)], result.Arguments);
        Assert.Equal(ValueKind.Object, result.Value.Kind);
        await session.DisposeAsync();
        Assert.Equal([returned, runtime.Root], runtime.Released);
        Assert.Single(runtime.Threads.Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCallStillOwnsReturnedReferences(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        object child = new();
        COMException failure = new("invoke", unchecked((int)0x80020009));
        var runtime = new Runtime
        {
            Operation = (arguments, _) =>
            {
                arguments[0] = child;
                if (cancel)
                {
                    cancellation.Cancel();
                    return null;
                }

                throw failure;
            }
        };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        Exception? error = await Record.ExceptionAsync(async () =>
            await session.InvokeWithReferencesAsync(root, "Change",
                [new(Value.Nothing, true)], null, cancellation.Token));
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        }
        else
        {
            Assert.Same(failure, error);
        }

        await session.DisposeAsync();
        Assert.Equal([child, runtime.Root], runtime.Released);
    }

    [Fact]
    public async Task ConversionFailureDoesNotSkipLaterOutReferenceOwnership()
    {
        object child = new();
        object returned = new();
        var runtime = new Runtime
        {
            Operation = (arguments, _) =>
            {
                arguments[0] = double.NaN;
                arguments[1] = child;
                return returned;
            }
        };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await session.InvokeWithReferencesAsync(root, "Change",
                [new(Value.Numeric(1), true), new(Value.Nothing, true)], null, default));
        await session.DisposeAsync();
        Assert.Equal([child, returned, runtime.Root], runtime.Released);
    }

    [Fact]
    public async Task UnchangedWrapperDoesNotAcquireInputReferenceAgain()
    {
        var runtime = new Runtime { Operation = (_, _) => null };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        WindowsMemberResult result = await session.InvokeWithReferencesAsync(root, "Change",
            [new(root, true)], null, default);
        Assert.Equal(root, Assert.Single(result.Arguments));
        await session.DisposeAsync();
        Assert.Equal([runtime.Root], runtime.Released);
    }

    [Fact]
    public async Task ArrayOutValuesDoNotModifyInputSnapshot()
    {
        var runtime = new Runtime
        {
            Operation = (arguments, _) =>
            {
                ComArrayValue input = Assert.IsType<ComArrayValue>(Assert.IsType<VariantWrapper>(arguments[0]).WrappedObject);
                Assert.Equal(1d, input.Values[0]);
                arguments[0] = new object?[] { 2d, "out" };
                return null;
            }
        };
        await using var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        var input = Value.FromArray(UwscArray.Create([0], [Value.Numeric(1)]));
        WindowsMemberResult result = await session.InvokeWithReferencesAsync(root, "Change",
            [new(input, true)], null, default);
        Assert.Equal([Value.Numeric(2), Value.String("out")], result.Arguments[0].AsArray().Values);
        Assert.Equal([Value.Numeric(1)], input.AsArray().Values);
    }
}
