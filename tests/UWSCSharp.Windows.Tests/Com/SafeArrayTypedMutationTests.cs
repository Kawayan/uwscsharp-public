using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 画面やCOMサーバーを使わず、生成した型付き配列の更新を検査します。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayTypedMutationTests
{
    private sealed class MutationRuntime : IComRuntime
    {
        private readonly object root = new();
        internal Func<object?>? Operation;

        public object Create(string name) => root;

        public object GetActive(string name) => root;

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign) => Operation!();

        public void Release(object target)
        {
        }
    }

    private sealed class Automation(UwscArray array) : IWindowsAutomation,
        IWindowsAutomationSession
    {
        private readonly object root = new();

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value.FromObject(root));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value.FromArray(array));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task AssignmentConvertsBeforeStoringTheValue(int type)
    {
        await using var session = new ComSession();
        var request = SafeArrayRequest.Read(
            [Value.Numeric(-2), Value.Numeric(-1), Value.Numeric(type)]);
        UwscArray original = (await session.CreateSafeArrayAsync(request,
            CancellationToken.None)).AsArray();
        Value input = type switch
        {
            7 or 8 or 11 => Value.Numeric(7),
            9 or 13 => Value.Nothing,
            10 => Value.FromError(-42),
            _ => Value.String("7")
        };
        UwscArray changed = original.Set([-2], input);
        Value value = changed.Get(-2);
        Assert.Equal((ushort)type, changed.NativeElementType);
        Assert.Equal([-2], changed.LowerBounds);
        Assert.Equal([-1], changed.UpperBounds);
        if (type == 12)
        {
            Assert.Equal(input, value);
        }
        else
        {
            Assert.Equal((ushort)type, value.NativeVariantType);
            Assert.Equal(type switch
            {
                8 => ValueKind.String,
                9 or 13 => ValueKind.Nothing,
                10 => ValueKind.Error,
                _ => ValueKind.Number
            }, value.Kind);
            if (type == 8)
            {
                Assert.Equal("7", value.Text);
            }
            else if (type == 10)
            {
                Assert.Equal(-42, value.AsErrorCode());
            }
            else if (type is not (9 or 13))
            {
                Assert.Equal(type == 11 ? 1 : 7, value.Number);
            }
        }
        if (type is not (9 or 13))
        {
            Assert.NotEqual(original.Values[0], changed.Values[0]);
        }
    }

    [Fact]
    public async Task FailedRowAssignmentKeepsTheOriginalArrayAndCanBeRetried()
    {
        await using var session = new ComSession();
        var request = SafeArrayRequest.Read(
            [Value.Numeric(-2), Value.Numeric(-1), Value.Numeric(5),
             Value.Numeric(6), Value.Numeric(17)]);
        UwscArray original = (await session.CreateSafeArrayAsync(request,
            CancellationToken.None)).AsArray();
        var invalid = UwscArray.CreateBounded([5], [6],
            [Value.String("7"), Value.Numeric(256)]);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            original.Set([-2], Value.FromArray(invalid)));
        Assert.IsType<COMException>(failure.InnerException);
        Assert.All(original.Values, value => Assert.Equal(0, value.Number));
        UwscArray changed = original.Set([-2, 6], Value.String("8"));
        Assert.Equal(8, changed.Get(-2, 6).Number);
        Assert.Equal((ushort)17, changed.Get(-2, 6).NativeVariantType);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public async Task EmptyArrayResizeUsesTypedDefaultsAndKeepsConversion(int type)
    {
        await using var session = new ComSession();
        var request = SafeArrayRequest.Read(
            [Value.Numeric(1), Value.Numeric(0), Value.Numeric(type)]);
        UwscArray empty = (await session.CreateSafeArrayAsync(request,
            CancellationToken.None)).AsArray();
        UwscArray resized = empty.Resize(1);
        Value initial = resized.Get(1);
        Assert.Equal(type == 8 ? ValueKind.String : ValueKind.Number, initial.Kind);
        Assert.Equal((ushort)type, initial.NativeVariantType);
        if (type == 8)
        {
            Assert.Null(initial.NativeScalar);
        }
        UwscArray changed = resized.Set([1], Value.String("7"));
        Assert.Equal((ushort)type, changed.Get(1).NativeVariantType);
        Assert.Equal("7", changed.Get(1).Display());
    }

    [Fact]
    public async Task CreationTokenDoesNotControlLaterUpdates()
    {
        await using var session = new ComSession();
        using var creation = new CancellationTokenSource();
        UwscArray array = (await session.CreateSafeArrayAsync(SafeArrayRequest.Read(
            [Value.Numeric(0), Value.Numeric(0), Value.Numeric(3)]), creation.Token)).AsArray();
        creation.Cancel();
        Assert.Equal(7, array.Set([0], Value.String("7")).Get(0).Number);
        using var mutation = new CancellationTokenSource();
        mutation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            array.Set([0], Value.String("8"), mutation.Token)));
        Assert.Equal(0, array.Get(0).Number);
    }

    [Fact]
    public async Task UpdatesAfterSessionDisposalAreRejected()
    {
        var session = new ComSession();
        UwscArray array = (await session.CreateSafeArrayAsync(SafeArrayRequest.Read(
            [Value.Numeric(0), Value.Numeric(0), Value.Numeric(3)]), CancellationToken.None)).AsArray();
        await session.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => array.Set([0], Value.String("7")));
        Assert.Equal(0, array.Get(0).Number);
    }

    [Fact]
    public async Task ReentrantMutationOnTheSessionStaDoesNotQueueItself()
    {
        var runtime = new MutationRuntime();
        var session = new ComSession(runtime);
        try
        {
            Value target = await session.CreateAsync("test", false, CancellationToken.None);
            UwscArray array = (await session.CreateSafeArrayAsync(SafeArrayRequest.Read(
                [Value.Numeric(0), Value.Numeric(0), Value.Numeric(3)]), CancellationToken.None)).AsArray();
            runtime.Operation = () => array.Set([0], Value.String("7")).Get(0).Number;
            Value result = await session.InvokeAsync(target, "Mutate", [], null,
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(7, result.Number);
            Assert.Equal(0, array.Get(0).Number);
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task InterpreterCanCatchANativeConversionFailureAndKeepTheArray()
    {
        await using var session = new ComSession();
        UwscArray array = (await session.CreateSafeArrayAsync(SafeArrayRequest.Read(
            [Value.Numeric(0), Value.Numeric(0), Value.Numeric(17)]), CancellationToken.None)).AsArray();
        List<string> output = [];
        await new Interpreter(windows: new Automation(array)).RunAsync("""
            a=CREATEOLEOBJ("array").Values
            TRY
                a[0]=256
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT a[0]
            a[0]="7"
            PRINT a[0]
            """, output.Add);
        Assert.Equal(["error", "0", "7"], output);
        Assert.Equal(0, array.Get(0).Number);
    }
}
