using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class ComSessionTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task UntypedComBooleansRetainLogicalType()
    {
        var runtime = new Runtime
        {
            Operation = (name, arguments, _) => name switch
            {
                "Enabled" => true,
                "Disabled" => false,
                "Values" => new object[] { true, false, 1d },
                "Accept" => Assert.IsType<double>(arguments[0]),
                _ => throw new InvalidOperationException(name)
            }
        };
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            fixture = CREATEOLEOBJ("fixture")
            enabled = fixture.Enabled
            disabled = fixture.Disabled
            items = fixture.Values
            PRINT VARTYPE(enabled)
            PRINT VARTYPE(disabled)
            PRINT VARTYPE(items[0])
            PRINT VARTYPE(items[1])
            PRINT VARTYPE(items[2])
            PRINT enabled + 0
            PRINT disabled + 0
            PRINT fixture.Accept(enabled)
            """, output.Add);
        Assert.Equal(["11", "11", "11", "11", "5", "1", "0", "1"], output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task ActiveLookupCanSucceedAfterAnUnavailableResult()
    {
        int attempts = 0;
        var runtime = new Runtime();
        runtime.GettingActive = () => ++attempts == 1
            ? throw ComOperationErrors.Record(
                new COMException(null, unchecked((int)0x800401E3)))
            : runtime.Root;
        runtime.Operation = (name, _, _) =>
        {
            Assert.Equal("Count", name);
            return 7;
        };
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: runtime);
        List<string> output = [];

        await new Interpreter(windows: automation).RunAsync("""
            TRY
                fixture=GETACTIVEOLEOBJ("fixture")
            EXCEPT
                PRINT "caught"
            ENDTRY
            fixture=GETACTIVEOLEOBJ("fixture")
            PRINT fixture.Count
            """, output.Add);

        Assert.Equal(["caught", "7"], output);
        Assert.Equal(["active:fixture", "active:fixture", "Count", "release"],
            runtime.Calls.Select(call => call.Operation));
        Assert.Same(runtime.Root, Assert.Single(runtime.Released));
    }

    private sealed class Runtime : IComRuntime
    {
        internal readonly object Root = new();
        internal readonly List<(string Operation, int Thread, ApartmentState Apartment)> Calls = [];
        internal readonly List<object> Released = [];
        internal Func<string, object?[], bool, object?>? Operation;
        internal Action<object>? Releasing;
        internal object? Created;
        internal Func<object>? Creating;
        internal Func<object>? GettingActive;

        private void Record(string name) => Calls.Add((name,
            Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

        public object Create(string name)
        {
            Record("create:" + name);
            return Creating?.Invoke() ?? Created ?? Root;
        }

        public object GetActive(string name)
        {
            Record("active:" + name);
            return GettingActive?.Invoke() ?? Created ?? Root;
        }

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            Assert.Same(Root, target);
            Record(name);
            return Operation?.Invoke(name, arguments, assign);
        }

        public void Release(object target)
        {
            Record("release");
            Released.Add(target);
            Releasing?.Invoke(target);
        }
    }

    private sealed class RegisteredRuntime : IComRuntime
    {
        private readonly NativeComRuntime native = new();
        private uint cookie;

        public object Create(string name)
        {
            object instance = native.Create(name);
            Guid id = Type.GetTypeFromProgID(name, true)!.GUID;
            try
            {
                Marshal.ThrowExceptionForHR(RegisterActiveObject(instance, ref id, 0, out cookie));
                return instance;
            }
            catch
            {
                native.Release(instance);
                throw;
            }
        }

        public object GetActive(string name) => native.GetActive(name);

        public object? Invoke(object target, string name, object?[] arguments, bool assign) =>
            native.Invoke(target, name, arguments, assign);

        public void Release(object target)
        {
            try
            {
                if (cookie != 0)
                {
                    uint registration = cookie;
                    cookie = 0;
                    Marshal.ThrowExceptionForHR(RevokeActiveObject(registration, 0));
                }
            }
            finally
            {
                native.Release(target);
            }
        }

        [DllImport("oleaut32.dll")]
        private static extern int RegisterActiveObject(
            [MarshalAs(UnmanagedType.IUnknown)] object instance,
            ref Guid id, uint flags, out uint cookie);

        [DllImport("oleaut32.dll")]
        private static extern int RevokeActiveObject(uint cookie, nint reserved);
    }

    [Fact]
    public async Task NativeActiveLookupFindsAndReleasesTemporaryRotRegistration()
    {
        const string progId = "Scripting.Dictionary";
        await using (var session = new ComSession(new RegisteredRuntime()))
        {
            Value created = await session.CreateAsync(progId, false, default);
            Value active = await session.CreateAsync(progId, true, default);
            Assert.Same(created.ObjectReference, active.ObjectReference);
            Assert.Equal(Value.Numeric(0),
                await session.InvokeAsync(active, "Count", [], null, default));
        }

        List<string> output = [];
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: new RegisteredRuntime());
        await new Interpreter(windows: automation).RunAsync(
            """
            TRY
                o=GETACTIVEOLEOBJ("Scripting.Dictionary")
                PRINT "unexpected"
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("registered",37)
            active=GETACTIVEOLEOBJ("Scripting.Dictionary")
            PRINT active.Item("registered")
            """, output.Add);
        Assert.Equal(["2", "37"], output);

        await using var verification = new ComSession();
        await Assert.ThrowsAnyAsync<COMException>(() =>
            verification.CreateAsync(progId, true, default).AsTask());
    }

    [Fact]
    public async Task InvalidProgIdDoesNotPreventLaterCreation()
    {
        await using var session = new ComSession();
        await Assert.ThrowsAnyAsync<COMException>(async () =>
            await session.CreateAsync("UWSCSharp.Tests.Missing." + Guid.NewGuid().ToString("N"), false, default));
        Value root = await session.CreateAsync("Scripting.Dictionary", false, default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(root, "Count", [], null, default));
    }

    [Fact]
    public async Task NativeDictionarySupportsMembersArraysAndCollections()
    {
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ =>
            throw new InvalidOperationException("入力操作は不要です。"))).RunAsync("""
            DIM d=CREATEOLEOBJ("Scripting.Dictionary")
            d.Add("日本語",2)
            d.Add("second",4)
            d.Item("日本語")=7
            PRINT d.cOuNt
            PRINT d.Item("日本語")+1
            PRINT d.Exists("second")
            DIM items=d.Items()
            PRINT items[0]
            PRINT LENGTH(d)
            PRINT GETOLEITEM(d)
            PRINT ALL_OLE_ITEM[0]
            FOR key IN d
                PRINT key
            NEXT
            FOR key IN GETOLEITEM(d)
                PRINT key
            NEXT
            d.RemoveAll()
            PRINT LENGTH(d)
            PRINT GETOLEITEM(d)
            PRINT LENGTH(ALL_OLE_ITEM)
            """, output.Add);
        Assert.Equal(["2", "8", "True", "7", "2", "2", "日本語",
            "日本語", "second", "日本語", "second", "0", "0", "0"], output);
    }

    [Fact]
    public async Task CreationActiveInvocationAndReleaseStayOnOneSta()
    {
        var runtime = new Runtime();
        runtime.Operation = (name, arguments, assign) =>
        {
            Assert.Equal("Value", name);
            Assert.True(assign);
            Assert.Equal(["key", 12d], arguments);
            return null;
        };
        var session = new ComSession(runtime);
        Value first = await session.CreateAsync("fixture", false, default);
        Value second = await session.CreateAsync("fixture", true, default);
        Assert.Same(first.ObjectReference, second.ObjectReference);
        Assert.Equal(Value.Nothing, await session.InvokeAsync(first, "Value",
            [Value.String("key")], Value.Numeric(12), default));
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal([runtime.Root, runtime.Root], runtime.Released);
        Assert.Single(runtime.Calls.Select(call => call.Thread).Distinct());
        Assert.All(runtime.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync(first, "Value", [], null, default));
    }

    [Fact]
    public async Task ConversionPreservesTagsAndArrayBounds()
    {
        var runtime = new Runtime();
        var array = Array.CreateInstance(typeof(object), [2, 2], [3, -1]);
        array.SetValue("a", 3, -1);
        array.SetValue(true, 3, 0);
        array.SetValue(DBNull.Value, 4, -1);
        array.SetValue(new ErrorWrapper(-42), 4, 0);
        runtime.Operation = (_, arguments, _) =>
        {
            Assert.Null(arguments[0]);
            Assert.IsType<DispatchWrapper>(arguments[1]);
            Assert.Same(DBNull.Value, arguments[2]);
            Assert.Same(Type.Missing, arguments[3]);
            Assert.Equal(-42, Assert.IsType<ErrorWrapper>(arguments[4]).ErrorCode);
            ComArrayValue input = Assert.IsType<ComArrayValue>(arguments[5]);
            Assert.Equal([0, 0], input.LowerBounds);
            Assert.Equal([1, 0], input.UpperBounds);
            Assert.Equal("source", input.Values[1]);
            Assert.Same(runtime.Root, arguments[6]);
            return array;
        };
        await using var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        Value result = await session.InvokeAsync(root, "Convert",
            [new(ValueKind.Empty), Value.Nothing, new(ValueKind.Null),
                new(ValueKind.Omitted), Value.FromError(-42),
                Value.FromArray(UwscArray.Create([1, 0], [Value.Numeric(1), Value.String("source")])), root],
            null, default);
        Assert.Equal([3, -1], result.AsArray().LowerBounds);
        Assert.Equal([4, 0], result.AsArray().UpperBounds);
        Assert.Equal([Value.String("a"), Value.Numeric(1), new(ValueKind.Null), Value.FromError(-42)],
            result.AsArray().Values);
    }

    [Fact]
    public async Task ForeignReferencesAreRejectedBeforeRuntimeInvocation()
    {
        var runtime = new Runtime();
        await using var first = new ComSession(runtime);
        await using var second = new ComSession(new Runtime());
        Value root = await first.CreateAsync("fixture", false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.InvokeAsync(root, "Value", [], null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.InvokeAsync(Value.Nothing, "Value", [], null, default));
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task CancellationWaitsForInvocationAndCleanupDrainsQueue()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        runtime.Operation = (_, _, _) =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return runtime.Root;
        };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        Task<Value> operation = session.InvokeAsync(root, "Blocking", [], null, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Task disposal = session.DisposeAsync().AsTask();
        Assert.False(operation.IsCompleted);
        Assert.False(disposal.IsCompleted);
        release.Set();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([runtime.Root, runtime.Root], runtime.Released);
    }

    [Theory]
    [InlineData("CREATEOLEOBJ", "create:fixture")]
    [InlineData("GETACTIVEOLEOBJ", "active:fixture")]
    public async Task CancelledScriptReleasesAcquiredComObject(
        string function, string acquisition)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        object Acquire()
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return runtime.Root;
        }

        runtime.Creating = Acquire;
        runtime.GettingActive = Acquire;
        List<string> output = [];
        var interpreter = new Interpreter(windows: new WindowsAutomation(
            _ => { }, comRuntime: runtime));
        Task operation = interpreter.RunAsync($$"""
            o={{function}}("fixture")
            PRINT "after"
            """, output.Add, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(output);
        Assert.Equal([runtime.Root], runtime.Released);
        Assert.Equal([acquisition, "release"],
            runtime.Calls.Select(call => call.Operation));
    }

    [Fact]
    public async Task PrecancelledOperationsDoNotAcquireObjects()
    {
        var runtime = new Runtime();
        await using var session = new ComSession(runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.CreateAsync("fixture", false, cancellation.Token));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task ReleaseFailureDoesNotSkipOtherAcquisitions()
    {
        var runtime = new Runtime();
        object child = new();
        runtime.Operation = (_, _, _) => child;
        runtime.Releasing = target =>
        {
            if (ReferenceEquals(target, child))
            {
                throw new InvalidOperationException("release");
            }
        };
        var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        await session.InvokeAsync(root, "Child", [], null, default);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(async () => await session.DisposeAsync());
        Assert.Equal("release", Assert.Single(error.InnerExceptions).Message);
        Assert.Equal([child, runtime.Root], runtime.Released);
    }

    [Fact]
    public async Task MemberFailureKeepsPrimaryExceptionWhenCleanupFails()
    {
        COMException failure = new("invoke", unchecked((int)0x80020009));
        var runtime = new Runtime
        {
            Operation = (_, _, _) => throw failure,
            Releasing = _ => throw new InvalidOperationException("release")
        };
        var interpreter = new Interpreter(windows: new WindowsAutomation(_ => { }, comRuntime: runtime));
        COMException result = await Assert.ThrowsAsync<COMException>(() => interpreter.RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            o.Fail()
            """, _ => { }));
        Assert.Same(failure, result);
        Assert.IsType<AggregateException>(result.Data["UWSCSharp.CleanupErrors"]);
        Assert.Single(runtime.Released);
    }

    [Fact]
    public async Task FailedEnumerationDisposesEnumeratorAndRetainsPreviousSnapshot()
    {
        var collection = new Collection();
        var runtime = new Runtime { Created = collection };
        await using IWindowsAutomationSession session = new WindowsAutomation(_ => { }, comRuntime: runtime).CreateSession();
        Value root = await session.InvokeAsync("CREATEOLEOBJ", [Value.String("fixture")], default);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETOLEITEM", [root], default));
        IReadOnlyList<Value> previous = session.AllOleItems;
        collection.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("GETOLEITEM", [root], default));
        Assert.Same(previous, session.AllOleItems);
        Assert.Equal(2, collection.Disposals);
        Assert.Equal(2, runtime.Released.Count(value => ReferenceEquals(value, collection.Underlying)));
    }

    [Fact]
    public async Task EnumerationFailureSurvivesBothEnumeratorCleanupFailures()
    {
        var collection = new Collection { Fail = true, DisposeFail = true };
        var runtime = new Runtime
        {
            Created = collection,
            Releasing = target =>
            {
                if (ReferenceEquals(target, collection.Underlying))
                {
                    throw new InvalidOperationException("release enumerator");
                }
            }
        };
        await using var session = new ComSession(runtime);
        Value root = await session.CreateAsync("fixture", false, default);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.EnumerateAsync(root, default));
        Assert.Equal("enumeration", error.Message);
        AggregateException cleanup = Assert.IsType<AggregateException>(error.Data["UWSCSharp.CleanupErrors"]);
        Assert.Equal(["dispose", "release enumerator"], cleanup.InnerExceptions.Select(exception => exception.Message));
    }

    private sealed class Collection : IEnumerable
    {
        internal bool Fail;
        internal bool DisposeFail;
        internal int Disposals;
        internal readonly object Underlying = new();

        public IEnumerator GetEnumerator() => new Enumerator(this);

        private sealed class Enumerator(Collection owner) : IEnumerator, IDisposable, ICustomAdapter
        {
            private int index;

            public object Current => index;

            public bool MoveNext()
            {
                if (owner.Fail && index == 1)
                {
                    throw new InvalidOperationException("enumeration");
                }

                return ++index <= 2;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                owner.Disposals++;
                if (owner.DisposeFail)
                {
                    throw new InvalidOperationException("dispose");
                }
            }

            public object GetUnderlyingObject() => owner.Underlying;
        }
    }
}
