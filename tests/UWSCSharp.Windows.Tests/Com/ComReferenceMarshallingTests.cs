using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class ComReferenceMarshallingTests
{
    [Fact]
    public async Task TypedArrayVarOutRetainsBoundsThroughScriptMutation()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        fixture.Operation = (arguments, count, result) =>
        {
            if (call++ == 0)
            {
                nint cell = Marshal.ReadIntPtr(DispatchFixture.Argument(arguments, 0, count), 8);
                ComVariantValue.Write(new ComArrayValue(3, [-2], [-1], [1d, 2d]), cell);
            }
            else
            {
                Assert.Equal(0x2003, Marshal.ReadInt16(arguments));
                Array received = Assert.IsAssignableFrom<Array>(Marshal.GetObjectForNativeVariant(arguments));
                Assert.Equal(-2, received.GetLowerBound(0));
                Assert.Equal(9, received.GetValue(-1));
            }
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM a
                o.Change(VAR a)
                PRINT LENGTH(a)
                PRINT a[-2]
                a[-1]=9
                o.Change(a)
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["2", "1"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrayObjectAcquisitionsSurviveLaterConversionFailure(bool invalid)
    {
        using var fixture = new DispatchFixture();
        fixture.Operation = (_, _, result) =>
        {
            Marshal.AddRef(fixture.Pointer);
            object instance = Marshal.GetObjectForIUnknown(fixture.Pointer);
            try
            {
                ComVariantValue.Write(new ComArrayValue(12, [-1], [1],
                    [new ComVariantValue(9, instance), invalid ? double.NaN : 42d,
                        new ComVariantValue(9, instance)]), result);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
                Marshal.Release(fixture.Pointer);
            }
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        if (invalid)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(root, "Change", [], null, default));
        }
        else
        {
            Value result = await session.InvokeAsync(root, "Change", [], null, default);
            Assert.Equal(root, result.AsArray().Get(-1));
            Assert.Equal(root, result.AsArray().Get(1));
        }

        Assert.Null(fixture.Failure);
    }

    [Theory]
    [InlineData(0x087f, "16777218")]
    [InlineData(0x047f, "16777216")]
    public async Task ComDispatchAppliesX87ControlDuringScriptRounding(
        int controlWord, string expectedRounded)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "floating-point-boundary",
            "x64", "DefDllAbiFixture.dll");
        using var module = NativeDllModule.Load(fixturePath);
        var readControl = new NativeDllCallSite(typeof(uint));
        NativeDllModule.Function x87 = module.Resolve("ReadX87Control", readControl);
        NativeDllModule.Function sse = module.Resolve("ReadSseControl", readControl);
        using var fixture = new DispatchFixture();
        var observations = new List<(uint X87, uint Sse, int Thread)>();
        fixture.Operation = (_, _, _) => observations.Add(
            ((uint)x87.Invoke()!, (uint)sse.Invoke()!, fixture.InvokeThread));
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync($$"""
            DIM o=CREATEOLEOBJ("fixture")
            o.Change()
            SET8087CW(${{controlWord:x4}})
            PRINT 16777216+1
            o.Change()
            SET8087CW(0)
            o.Change()
            """, output.Add);

        Assert.Null(fixture.Failure);
        Assert.Equal([expectedRounded], output);
        Assert.Equal(3, observations.Count);
        (uint x87Before, uint sseBefore, int thread) = observations[0];
        Assert.Equal(((uint)controlWord, sseBefore, thread), observations[1]);
        Assert.Equal((x87Before, sseBefore, thread), observations[2]);
        Assert.Equal(ApartmentState.STA, fixture.InvokeApartment);
    }

    [Theory]
    [InlineData(0x087f, 0x047f)]
    [InlineData(0x047f, 0x087f)]
    [InlineData(0x087f, null)]
    [InlineData(0x047f, null)]
    public async Task ReentrantComCallbackRestoresOuterControlWord(
        int outerControl, int? innerControl)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory,
            "floating-point-boundary", "x64", "DefDllAbiFixture.dll");
        using var module = NativeDllModule.Load(fixturePath);
        var readControl = new NativeDllCallSite(typeof(uint));
        NativeDllModule.Function x87 = module.Resolve("ReadX87Control", readControl);
        NativeDllModule.Function sse = module.Resolve("ReadSseControl", readControl);
        using var fixture = new DispatchFixture();
        await using var session = new ComSession(new Runtime(fixture));
        Value target = await session.CreateAsync("fixture", false, default);
        var observations = new List<(uint X87, uint Sse, int Thread)>();
        fixture.Operation = (arguments, count, _) =>
        {
            Assert.Equal(1u, count);
            double stage = Assert.IsType<double>(
                Marshal.GetObjectForNativeVariant(arguments));
            Observe();
            if (stage == 1)
            {
                session.WaitForEventCallback(Reenter());
                Observe();
            }
        };

        await session.InvokeAsync(target, "Change", [Value.Numeric(0)],
            null, default);
        await session.InvokeAsync(target, "Change", [Value.Numeric(1)],
            null, default, (ushort)outerControl);
        await session.InvokeAsync(target, "Change", [Value.Numeric(3)],
            null, default);

        Assert.Null(fixture.Failure);
        Assert.Equal(5, observations.Count);
        (uint before, uint sseBefore, int thread) = observations[0];
        Assert.Equal(((uint)outerControl, sseBefore, thread), observations[1]);
        Assert.Equal(((uint?)innerControl ?? before, sseBefore, thread), observations[2]);
        Assert.Equal(((uint)outerControl, sseBefore, thread), observations[3]);
        Assert.Equal((before, sseBefore, thread), observations[4]);
        Assert.Equal(ApartmentState.STA, fixture.InvokeApartment);

        void Observe() => observations.Add(((uint)x87.Invoke()!,
            (uint)sse.Invoke()!, Environment.CurrentManagedThreadId));

        async Task Reenter()
        {
            await Task.Yield();
            await session.InvokeAsync(target, "Change", [Value.Numeric(2)],
                null, default, (ushort?)innerControl)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(0x087f, 0x047f)]
    [InlineData(0x047f, 0x087f)]
    public async Task ScriptComEventReentryRestoresOuterControlWord(
        int outerControl, int innerControl)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory,
            "floating-point-boundary", "x64", "DefDllAbiFixture.dll");
        using var module = NativeDllModule.Load(fixturePath);
        var readControl = new NativeDllCallSite(typeof(uint));
        NativeDllModule.Function x87 = module.Resolve("ReadX87Control", readControl);
        NativeDllModule.Function sse = module.Resolve("ReadSseControl", readControl);
        using var fixture = new DispatchFixture();
        var runtime = new Runtime(fixture);
        var observations = new List<(uint X87, uint Sse, int Thread)>();
        fixture.Operation = (arguments, count, _) =>
        {
            Assert.Equal(1u, count);
            double stage = Assert.IsType<double>(
                Marshal.GetObjectForNativeVariant(arguments));
            Observe();
            if (stage == 1)
            {
                Assert.IsType<TestEventConnection>(runtime.EventConnection).Fire();
                Observe();
            }
        };

        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: runtime)).RunAsync($$"""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            obj.Change(0)
            OLEEVENT(obj,"Events","Changed","Changed")
            SET8087CW(${{outerControl:x4}})
            obj.Change(1)
            OLEEVENT(obj)
            SET8087CW(0)
            obj.Change(3)
            PROCEDURE Changed()
                SET8087CW(${{innerControl:x4}})
                obj.Change(2)
                PRINT "event"
            FEND
            """, output.Add).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(fixture.Failure);
        Assert.Equal(["event"], output);
        Assert.True(Assert.IsType<TestEventConnection>(runtime.EventConnection).Disposed);
        Assert.Equal(5, observations.Count);
        (uint before, uint sseBefore, int thread) = observations[0];
        Assert.Equal(((uint)outerControl, sseBefore, thread), observations[1]);
        Assert.Equal(((uint)innerControl, sseBefore, thread), observations[2]);
        Assert.Equal(((uint)outerControl, sseBefore, thread), observations[3]);
        Assert.Equal((before, sseBefore, thread), observations[4]);

        void Observe() => observations.Add(((uint)x87.Invoke()!,
            (uint)sse.Invoke()!, Environment.CurrentManagedThreadId));
    }

    [ComImport]
    [Guid("22F03340-547D-101B-8E65-08002B2BD119")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateErrorInfo
    {
        [PreserveSig]
        int SetGUID(ref Guid id);

        [PreserveSig]
        int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        [PreserveSig]
        int SetHelpFile([MarshalAs(UnmanagedType.LPWStr)] string file);

        [PreserveSig]
        int SetHelpContext(uint context);
    }

    [DllImport("oleaut32.dll")]
    private static extern int CreateErrorInfo(out ICreateErrorInfo info);

    [DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(uint reserved, ComErrorInfo.IErrorInfo info);

    /// <summary>
    /// 登録や外部アプリを使わずIDispatchの実引数を観測するfixtureです。
    /// </summary>
    private sealed class DispatchFixture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Parameters
        {
            internal nint Arguments;
            internal nint NamedIds;
            internal uint Count;
            internal uint NamedCount;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Query(nint self, ref Guid id, out nint result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint Reference(nint self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TypeCount(nint self, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TypeInfo(nint self, uint index, uint locale, out nint info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Names(nint self, ref Guid id, nint names, uint count,
            uint locale, nint ids);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Invoke(nint self, int id, ref Guid iid, uint locale,
            ushort flags, ref Parameters parameters, nint result, nint exception, nint error);

        private readonly Delegate[] callbacks;
        private readonly nint table;
        private int references = 1;
        internal readonly nint Pointer;
        internal Exception? Failure;
        internal int AddRefCalls;
        internal int InvokeThread;
        internal ApartmentState InvokeApartment;
        internal Action<nint, uint, nint>? Operation;
        internal Action<nint, uint, nint, nint, uint, ushort>? DetailedOperation;
        internal readonly Dictionary<string, int> ArgumentIds = new(StringComparer.OrdinalIgnoreCase);
        internal int HResult;
        internal Func<System.Runtime.InteropServices.ComTypes.EXCEPINFO>? ExceptionInfo;
        internal Action? BeforeReturn;
        internal uint ArgumentError;
        internal int MemberId = 1;
        internal string MemberName = "Change";

        internal DispatchFixture()
        {
            callbacks = [new Query(QueryInterface), new Reference(AddRef), new Reference(Release),
                new TypeCount(GetTypeCount), new TypeInfo(GetTypeInfo), new Names(GetNames), new Invoke(Call)];
            table = Marshal.AllocCoTaskMem(callbacks.Length * IntPtr.Size);
            for (int i = 0; i < callbacks.Length; i++)
            {
                Marshal.WriteIntPtr(table, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callbacks[i]));
            }

            Pointer = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, table);
        }

        private int QueryInterface(nint self, ref Guid id, out nint result)
        {
            if (id == new Guid("00000000-0000-0000-C000-000000000046")
                || id == new Guid("00020400-0000-0000-C000-000000000046"))
            {
                result = self;
                AddRef(self);
                return 0;
            }

            result = 0;
            return unchecked((int)0x80004002);
        }

        private uint AddRef(nint self)
        {
            Interlocked.Increment(ref AddRefCalls);
            return (uint)Interlocked.Increment(ref references);
        }

        private uint Release(nint self) => (uint)Interlocked.Decrement(ref references);

        private static int GetTypeCount(nint self, out uint count)
        {
            count = 0;
            return 0;
        }

        private static int GetTypeInfo(nint self, uint index, uint locale, out nint info)
        {
            info = 0;
            return unchecked((int)0x80004001);
        }

        private int GetNames(nint self, ref Guid id, nint names, uint count,
            uint locale, nint ids)
        {
            if (count >= 1 && string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(names)),
                MemberName, StringComparison.OrdinalIgnoreCase))
            {
                Marshal.WriteInt32(ids, 1);
                for (int i = 1; i < count; i++)
                {
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size))!;
                    if (!ArgumentIds.TryGetValue(name, out int argumentId))
                    {
                        return unchecked((int)0x80020006);
                    }

                    Marshal.WriteInt32(ids, i * sizeof(int), argumentId);
                }

                return 0;
            }

            return unchecked((int)0x80020006);
        }

        private int Call(nint self, int id, ref Guid iid, uint locale, ushort flags,
            ref Parameters parameters, nint result, nint exception, nint error)
        {
            try
            {
                Assert.Equal(MemberId, id);
                InvokeThread = Environment.CurrentManagedThreadId;
                InvokeApartment = Thread.CurrentThread.GetApartmentState();
                if (DetailedOperation is not null)
                {
                    DetailedOperation(parameters.Arguments, parameters.Count, result,
                        parameters.NamedIds, parameters.NamedCount, flags);
                }
                else
                {
                    Assert.Equal(0u, parameters.NamedCount);
                    Operation!(parameters.Arguments, parameters.Count, result);
                }
                if (ExceptionInfo is not null)
                {
                    Marshal.StructureToPtr(ExceptionInfo(), exception, false);
                }

                if (error != 0)
                {
                    Marshal.WriteInt32(error, unchecked((int)ArgumentError));
                }

                BeforeReturn?.Invoke();
                return HResult;
            }
            catch (Exception ex)
            {
                // assertionの例外をunmanaged境界へ伝播させません。
                Failure = ex;
                return unchecked((int)0x80004005);
            }
        }

        internal static nint Argument(nint arguments, int sourceIndex, uint count) =>
            arguments + ((int)count - 1 - sourceIndex) * (IntPtr.Size == 8 ? 24 : 16);

        internal static void Replace(nint variant, object? value)
        {
            Marshal.ThrowExceptionForHR(VariantClear(variant));
            Marshal.GetNativeVariantForObject(value, variant);
        }

        public void Dispose()
        {
            Assert.Equal(1, references);
            Release(Pointer);
            Marshal.FreeCoTaskMem(Pointer);
            Marshal.FreeCoTaskMem(table);
            GC.KeepAlive(callbacks);
        }

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(nint variant);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task EventWritebackSharesBudgetAcrossReferenceArguments(int extra)
    {
        using var fixture = new DispatchFixture();
        var runtime = new Runtime(fixture);
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        using var memory = new ComEventArgumentTests.Frame(2);
        memory.Set(0, 3, variantReference: true);
        memory.Set(1, 7, variantReference: true);

        int half = UwscArray.MaximumElements / 2;
        Value first = CreateArray(half);
        Value second = extra == 0 ? first : CreateArray(half + extra);
        await session.ConnectValueEventAsync(target, "Events", "Changed",
            (_, _) => ValueTask.FromResult<IReadOnlyList<Value>>([first, second]),
            default);
        Exception? failure = null;
        fixture.Operation = (_, _, _) =>
            failure = Record.Exception(() => Assert.IsType<TestEventConnection>(
                runtime.EventConnection).Fire(memory.Parameters));

        await session.InvokeAsync(target, "Change", [], null, default);
        Assert.Null(fixture.Failure);
        NativeComEventArguments frame = memory.Open();
        if (extra == 0)
        {
            Assert.Null(failure);
            for (int index = 0; index < 2; index++)
            {
                Assert.Equal(half - 1, Assert.IsType<ComArrayValue>(
                    frame.Read(index).Value).UpperBounds[0]);
            }
        }
        else
        {
            InvalidOperationException overflow =
                Assert.IsType<InvalidOperationException>(failure);
            Assert.Contains("COM値の累積要素数が上限を超えました",
                overflow.Message);
            Assert.Equal(3, frame.Read(0).Value);
            Assert.Equal(7, frame.Read(1).Value);
        }

        static Value CreateArray(int count) => Value.FromArray(
            UwscArray.CreateBounded([0], [count - 1],
                Enumerable.Repeat(Value.Empty, count)));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task EventReadLimitRejectsSecondArrayBeforeReadingItsElements()
    {
        using var fixture = new DispatchFixture();
        using var element = new DispatchFixture();
        var runtime = new Runtime(fixture);
        await using var session = new ComSession(runtime);
        Value target = await session.CreateAsync("fixture", false, default);
        int callbacks = 0;
        await session.ConnectValueEventAsync(target, "Events", "Changed",
            (values, _) =>
            {
                callbacks++;
                return ValueTask.FromResult(values);
            }, default);

        Exception? failure = null;
        int elementReads = -1;
        fixture.Operation = (_, _, _) =>
        {
            int half = UwscArray.MaximumElements / 2;
            object instance = Marshal.GetObjectForIUnknown(element.Pointer);
            try
            {
                using var memory = new ComEventArgumentTests.Frame(2);
                memory.Set(0, new ComArrayValue(3, [0], [half - 1],
                    [.. Enumerable.Repeat<object?>(0, half)]),
                    variantReference: true);
                object?[] elements = new object?[half + 1];
                elements[0] = new ComVariantValue(9, instance);
                memory.Set(1, new ComArrayValue(12, [0], [half], elements),
                    variantReference: true);

                TestEventConnection connection = Assert.IsType<TestEventConnection>(
                    runtime.EventConnection);
                int before = Volatile.Read(ref element.AddRefCalls);
                failure = Record.Exception(() => connection.Fire(memory.Parameters));
                elementReads = Volatile.Read(ref element.AddRefCalls) - before;
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        };

        await session.InvokeAsync(target, "Change", [], null, default);
        Assert.Null(fixture.Failure);
        InvalidOperationException overflow =
            Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("COM値の累積要素数が上限を超えました", overflow.Message);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, elementReads);
    }

    private sealed class TestEventConnection(NativeComEventHandler callback)
        : IComEventConnection
    {
        internal bool Disposed
        {
            get; private set;
        }

        internal void Fire(
            System.Runtime.InteropServices.ComTypes.DISPPARAMS parameters = default)
        {
            Assert.False(Disposed);
            callback(ref parameters);
        }

        public Exception? TakeCallbackFailure() => null;

        public void Dispose() => Disposed = true;
    }

    private sealed class Runtime(DispatchFixture fixture) : IComRuntime
    {
        private readonly NativeComRuntime native = new();

        internal TestEventConnection? EventConnection
        {
            get; private set;
        }

        public object Create(string name)
        {
            object result = Marshal.GetObjectForIUnknown(fixture.Pointer);
            Assert.True(Marshal.IsComObject(result));
            return result;
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign) =>
            native.Invoke(target, name, arguments, assign);

        public object? Invoke(object target, string name, object?[] arguments, bool assign,
            IReadOnlyList<bool> byReference) => native.Invoke(target, name, arguments, assign, byReference);

        public object? Invoke(object target, string name, object?[] arguments, bool assign,
            IReadOnlyList<bool> byReference, IReadOnlyList<string?> argumentNames) =>
            native.Invoke(target, name, arguments, assign, byReference, argumentNames);

        public object? Invoke(object target, string name, object?[] arguments,
            bool assign, IReadOnlyList<bool> byReference,
            IReadOnlyList<string?> argumentNames, ushort? controlWord) =>
            native.Invoke(target, name, arguments, assign, byReference,
                argumentNames, controlWord);

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("Events", interfaceName);
            Assert.Equal("Changed", eventName);
            Assert.Null(EventConnection);
            EventConnection = new TestEventConnection(callback);
            return EventConnection;
        }

        public void Release(object target) => native.Release(target);
    }

    [Fact]
    public async Task NativeDispatchReceivesByrefVariantsAndChangesOutputTypes()
    {
        using var fixture = new DispatchFixture();
        fixture.Operation = (arguments, count, result) =>
        {
            Assert.Equal(3u, count);
            nint first = DispatchFixture.Argument(arguments, 0, count);
            nint middle = DispatchFixture.Argument(arguments, 1, count);
            nint last = DispatchFixture.Argument(arguments, 2, count);
            Assert.Equal(0x400c, Marshal.ReadInt16(first));
            Assert.Equal(0x400c, Marshal.ReadInt16(last));
            Assert.Equal(5, Marshal.ReadInt16(middle));
            nint firstValue = Marshal.ReadIntPtr(first, 8);
            nint lastValue = Marshal.ReadIntPtr(last, 8);
            Assert.Equal("before", Marshal.GetObjectForNativeVariant(firstValue));
            Assert.Equal(2d, Marshal.GetObjectForNativeVariant(lastValue));
            Assert.Equal(3d, Marshal.GetObjectForNativeVariant(middle));
            DispatchFixture.Replace(firstValue, 10d);
            DispatchFixture.Replace(lastValue, "changed");
            DispatchFixture.Replace(result, 42d);
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM x="before"
                DIM y=2
                PRINT o.Change(VAR x,3,VAR y)
                PRINT x
                PRINT y
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["42", "10", "changed"], output);
        Assert.NotEqual(0, fixture.InvokeThread);
        Assert.Equal(ApartmentState.STA, fixture.InvokeApartment);
    }

    [Fact]
    [Trait("Category", "Headless")]
    public async Task IeGetFramePublishesCancellationAfterNativeInvokeReturns()
    {
        using var cancellation = new CancellationTokenSource();
        using var mayReturn = new ManualResetEventSlim();
        using var fixture = new DispatchFixture();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.MemberName = "document";
        fixture.Operation = (_, _, _) => { };
        fixture.BeforeReturn = () =>
        {
            entered.SetResult();
            Assert.True(mayReturn.Wait(TimeSpan.FromSeconds(10)));
        };

        await using var session = new ComSession(new Runtime(fixture));
        Value target = await session.CreateAsync("fixture", false, default);
        var arguments = new CallArguments([target, Value.Numeric(1)]);
        Task<Value> lookup = session.GetHtmlFrameAsync(arguments,
            cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(lookup.IsCompleted);
        }
        finally
        {
            mayReturn.Set();
        }

        OperationCanceledException failure =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Null(fixture.Failure);

        fixture.BeforeReturn = null;
        Value result = await session.GetHtmlFrameAsync(arguments, default);
        Assert.Equal(ValueKind.Empty, result.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailureOrCancellationDoesNotWriteBack(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new DispatchFixture
        {
            HResult = cancel ? 0 : unchecked((int)0x80004005)
        };
        fixture.Operation = (arguments, count, result) =>
        {
            Assert.Equal(1u, count);
            nint argument = DispatchFixture.Argument(arguments, 0, count);
            Assert.Equal(0x400c, Marshal.ReadInt16(argument));
            DispatchFixture.Replace(Marshal.ReadIntPtr(argument, 8), "changed");
            if (cancel)
            {
                cancellation.Cancel();
            }
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                OPTION OPTFINALLY
                DIM o=CREATEOLEOBJ("fixture")
                DIM x="before"
                TRY
                    o.Change(VAR x)
                FINALLY
                    PRINT x
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Null(fixture.Failure);
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        }
        else
        {
            ScriptException scriptFailure = Assert.IsType<ScriptException>(failure);
            Assert.IsType<COMException>(scriptFailure.InnerException?.InnerException);
        }

        Assert.Equal(["before"], output);
    }

    [Fact]
    public async Task NativeObjectAliasOutKeepsIdentityAndBalancesAcquisitions()
    {
        using var fixture = new DispatchFixture();
        fixture.Operation = (arguments, count, result) =>
        {
            Assert.Equal(1u, count);
            nint argument = DispatchFixture.Argument(arguments, 0, count);
            Assert.Equal(0x400c, Marshal.ReadInt16(argument));
            nint value = Marshal.ReadIntPtr(argument, 8);
            Assert.Equal(9, Marshal.ReadInt16(value));
            DispatchFixture.Replace(result, 1d);
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM alias=o
                o.Change(VAR alias)
                PRINT alias=o
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["1"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedAndPositionalArgumentsKeepDispIdsAndVarCopyback(
        bool controlled)
    {
        using var fixture = new DispatchFixture();
        fixture.ArgumentIds.Add("First", 100);
        fixture.ArgumentIds.Add("Second", 200);
        fixture.ArgumentIds.Add("Third", 300);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, _) =>
        {
            Assert.Equal(4u, count);
            Assert.Equal(3u, namedCount);
            int stride = IntPtr.Size == 8 ? 24 : 16;
            var seen = new HashSet<int>();
            for (int i = 0; i < namedCount; i++)
            {
                int id = Marshal.ReadInt32(ids, i * sizeof(int));
                Assert.True(seen.Add(id));
                nint argument = arguments + i * stride;
                if (id == 100)
                {
                    Assert.Equal(8d, Marshal.GetObjectForNativeVariant(argument));
                }
                else
                {
                    Assert.Equal(0x400c, Marshal.ReadInt16(argument));
                    nint value = Marshal.ReadIntPtr(argument, 8);
                    Assert.Equal(id == 200 ? 1d : 2d, Marshal.GetObjectForNativeVariant(value));
                    DispatchFixture.Replace(value, id == 200 ? 10d : 20d);
                }
            }

            Assert.Equal([100, 200, 300], seen.Order());
            Assert.Equal(4d, Marshal.GetObjectForNativeVariant(arguments + 3 * stride));
            DispatchFixture.Replace(result, 42d);
        };
        var output = new List<string>();
        string setup = controlled ? "SET8087CW($087f)" : "";
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync($$"""
                {{setup}}
                DIM o=CREATEOLEOBJ("fixture")
                DIM x=1
                DIM y=2
                PRINT o.Change(Second:=VAR x,First=8,4,VAR Third=y)
                PRINT x
                PRINT y
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["42", "10", "20"], output);
    }

    [Fact]
    public async Task NamedOmissionIsMarshalledAsParamNotFound()
    {
        using var fixture = new DispatchFixture();
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, _) =>
        {
            Assert.Equal(2u, count);
            Assert.Equal(1u, namedCount);
            Assert.Equal(100, Marshal.ReadInt32(ids));
            Assert.Equal(10, Marshal.ReadInt16(arguments));
            Assert.Equal(unchecked((int)0x80020004), Marshal.ReadInt32(arguments, 8));
            Assert.Equal(5d, Marshal.GetObjectForNativeVariant(arguments + (IntPtr.Size == 8 ? 24 : 16)));
            DispatchFixture.Replace(result, 77d);
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                PRINT o.Change(First=,5)
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["77"], output);
    }

    [Fact]
    public async Task UnknownNamedArgumentDoesNotInvokeOrWriteBack()
    {
        using var fixture = new DispatchFixture();
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM x=1
                TRY
                    o.Change(Unknown=VAR x)
                FINALLY
                    PRINT x
                ENDTRY
                """, output.Add));
        Assert.Null(fixture.Failure);
        ScriptException scriptFailure = Assert.IsType<ScriptException>(failure);
        Assert.Equal(4, scriptFailure.Line);
        Assert.IsType<COMException>(scriptFailure.InnerException?.InnerException);
        Assert.Equal(0, fixture.InvokeThread);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task NamedIndexedSetterKeepsPropertyPutAndVarFlags()
    {
        using var fixture = new DispatchFixture();
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, flags) =>
        {
            Assert.Equal(3u, count);
            Assert.Equal(2u, namedCount);
            Assert.Equal(12, flags);
            int stride = IntPtr.Size == 8 ? 24 : 16;
            var seen = new HashSet<int>();
            for (int i = 0; i < namedCount; i++)
            {
                int id = Marshal.ReadInt32(ids, i * sizeof(int));
                seen.Add(id);
                nint argument = arguments + i * stride;
                if (id == -3)
                {
                    Assert.Equal(9d, Marshal.GetObjectForNativeVariant(argument));
                }
                else
                {
                    Assert.Equal(100, id);
                    Assert.Equal(0x400c, Marshal.ReadInt16(argument));
                    DispatchFixture.Replace(Marshal.ReadIntPtr(argument, 8), 11d);
                }
            }

            Assert.Equal([-3, 100], seen.Order());
            Assert.Equal(3d, Marshal.GetObjectForNativeVariant(arguments + 2 * stride));
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM x=1
                o.Change(3,First=VAR x)=9
                PRINT x
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["11"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedCallFailureOrCancellationDoesNotPublishOutValues(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new DispatchFixture { HResult = cancel ? 0 : unchecked((int)0x80004005) };
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, _) =>
        {
            Assert.Equal(2u, count);
            Assert.Equal(1u, namedCount);
            Assert.Equal(100, Marshal.ReadInt32(ids));
            Assert.Equal(0x400c, Marshal.ReadInt16(arguments));
            DispatchFixture.Replace(Marshal.ReadIntPtr(arguments, 8), 10d);
            if (cancel)
            {
                cancellation.Cancel();
            }
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                OPTION OPTFINALLY
                DIM o=CREATEOLEOBJ("fixture")
                DIM x=1
                TRY
                    o.Change(3,First=VAR x)
                FINALLY
                    PRINT x
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Null(fixture.Failure);
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        }
        else
        {
            ScriptException scriptFailure = Assert.IsType<ScriptException>(failure);
            Assert.IsType<COMException>(scriptFailure.InnerException?.InnerException);
        }

        Assert.Equal(["1"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedSetterFailureOrCancellationCleansVariantsWithoutCopyback(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new DispatchFixture { HResult = cancel ? 0 : unchecked((int)0x80004005) };
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, _) =>
        {
            Assert.Equal(2u, count);
            Assert.Equal(2u, namedCount);
            int stride = IntPtr.Size == 8 ? 24 : 16;
            for (int i = 0; i < namedCount; i++)
            {
                if (Marshal.ReadInt32(ids, i * sizeof(int)) == 100)
                {
                    nint argument = arguments + i * stride;
                    Assert.Equal(0x400c, Marshal.ReadInt16(argument));
                    DispatchFixture.Replace(Marshal.ReadIntPtr(argument, 8), "changed");
                }
            }

            if (cancel)
            {
                cancellation.Cancel();
            }
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                OPTION OPTFINALLY
                DIM o=CREATEOLEOBJ("fixture")
                DIM x="before"
                TRY
                    o.Change(First=VAR x)=9
                FINALLY
                    PRINT x
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Null(fixture.Failure);
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
        }
        else
        {
            ScriptException scriptFailure = Assert.IsType<ScriptException>(failure);
            Assert.IsType<COMException>(scriptFailure.InnerException?.InnerException);
        }

        Assert.Equal(["before"], output);
    }

    [Fact]
    public async Task NamedSetterObjectOutRetainsIdentityAndBalancesNativeReferences()
    {
        using var fixture = new DispatchFixture();
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (arguments, count, result, ids, namedCount, _) =>
        {
            Assert.Equal(2u, count);
            Assert.Equal(2u, namedCount);
            int stride = IntPtr.Size == 8 ? 24 : 16;
            for (int i = 0; i < namedCount; i++)
            {
                if (Marshal.ReadInt32(ids, i * sizeof(int)) == 100)
                {
                    nint argument = arguments + i * stride;
                    Assert.Equal(0x400c, Marshal.ReadInt16(argument));
                    Assert.Equal(9, Marshal.ReadInt16(Marshal.ReadIntPtr(argument, 8)));
                }
            }
        };
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM alias=o
                o.Change(First=VAR alias)=9
                PRINT alias=o
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task NativeEmptyNullNothingAndErrorRemainDistinct()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        ComVariantValue[] values = [new(0, null), new(1, DBNull.Value), new(9, null),
            new(10, new ErrorWrapper(-42))];
        fixture.Operation = (_, _, result) => ComVariantValue.Write(values[call++], result);
        var output = new List<string>();
        Exception? failure = await Record.ExceptionAsync(() => new Interpreter(windows:
            new WindowsAutomation(_ => { }, comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                DIM emptyValue=o.Change()
                DIM nullValue=o.Change()
                DIM nothingValue=o.Change()
                DIM errorValue=o.Change()
                PRINT emptyValue=EMPTY
                PRINT emptyValue=NOTHING
                PRINT nullValue
                PRINT nothingValue
                PRINT errorValue
                """, output.Add));
        Assert.Null(fixture.Failure);
        Assert.Null(failure);
        Assert.Equal(["1", "0", "NULL", "NOTHING", "ERROR"], output);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task NativeScalarRoundtripKeepsOriginalTypeAndPrecision(int type)
    {
        using var fixture = new DispatchFixture();
        object scalar = type switch
        {
            3 or 22 => 123,
            4 => 1.25f,
            6 => 922337203685477.5807m,
            7 => DateTime.FromOADate(45000.25),
            11 => true,
            14 => 1234567890123456789.123456789m,
            20 => long.MaxValue,
            21 => ulong.MaxValue,
            23 => uint.MaxValue,
            _ => throw new InvalidOperationException()
        };
        double number = scalar is DateTime date ? date.ToOADate()
            : scalar is bool flag ? flag ? 1 : 0 : Convert.ToDouble(scalar);
        int call = 0;
        fixture.Operation = (arguments, count, result) =>
        {
            if (call++ == 0)
            {
                ComVariantValue.Write(new ComVariantValue((ushort)type, scalar), result);
            }
            else
            {
                Assert.Equal(1u, count);
                Assert.Equal(type, Marshal.ReadInt16(arguments));
                Assert.Equal(scalar, Marshal.GetObjectForNativeVariant(arguments));
            }
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Value value = await session.InvokeAsync(root, "Change", [], null, default);
        Assert.Equal(Value.Numeric(number), value);
        Assert.Equal(Value.Numeric(number).GetHashCode(), value.GetHashCode());
        Value copied = UwscArray.Create([0], [value]).Get([0]);
        await session.InvokeAsync(root, "Change", [copied], null, default);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    public async Task ModifiedNativeScalarDoesNotResendStalePayload()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        fixture.Operation = (arguments, _, result) =>
        {
            if (call++ == 0)
            {
                ComVariantValue.Write(new ComVariantValue(3, 123), result);
            }
            else
            {
                Assert.Equal(5, Marshal.ReadInt16(arguments));
                Assert.Equal(124d, Marshal.GetObjectForNativeVariant(arguments));
            }
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Value value = await session.InvokeAsync(root, "Change", [], null, default);
        await session.InvokeAsync(root, "Change", [value with { Number = 124 }], null, default);
        Assert.Null(fixture.Failure);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 0x087f)]
    [InlineData(true, 0x087f)]
    public async Task ExcepInfoAndDeferredFillInPreserveDetails(bool deferred,
        int? controlWord)
    {
        using var fixture = new DispatchFixture { HResult = unchecked((int)0x80020009) };
        fixture.Operation = (_, _, _) => { };
        int fills = 0;
        ComErrorInfo.FillIn callback = (ref System.Runtime.InteropServices.ComTypes.EXCEPINFO info) =>
        {
            fills++;
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            info.bstrDescription = "詳細エラー";
            info.bstrSource = "fixture";
            info.bstrHelpFile = "fixture-help.chm";
            info.dwHelpContext = 42;
            info.scode = unchecked((int)0x80040201);
            return 0;
        };
        fixture.ExceptionInfo = () => deferred
            ? new()
            {
                pfnDeferredFillIn = Marshal.GetFunctionPointerForDelegate(callback),
                wCode = 1200
            }
            : new()
            {
                bstrDescription = "詳細エラー",
                bstrSource = "fixture",
                bstrHelpFile = "fixture-help.chm",
                dwHelpContext = 42,
                scode = unchecked((int)0x80040201)
            };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync(root, "Change", [], null, default,
                (ushort?)controlWord));
        Assert.Null(fixture.Failure);
        Assert.Equal("詳細エラー", error.Message);
        Assert.Equal("fixture", error.Source);
        Assert.Equal("fixture-help.chm", error.HelpLink);
        Assert.Equal(unchecked((int)0x80040201), error.ErrorCode);
        Assert.Equal(unchecked((int)0x80020009), error.Data["UWSCSharp.ComInvokeHResult"]);
        Assert.Equal(42, error.Data["UWSCSharp.ComHelpContext"]);
        Assert.Equal(deferred ? 1 : 0, fills);
    }

    [Fact]
    public async Task NamedArgumentErrorPositionMapsBackToSourceOrder()
    {
        using var fixture = new DispatchFixture
        {
            HResult = unchecked((int)0x80020005),
            ArgumentError = 0
        };
        fixture.ArgumentIds.Add("First", 100);
        fixture.DetailedOperation = (_, _, _, _, _, _) => { };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeWithReferencesAsync(root, "Change",
                [new(Value.Numeric(1)), new(Value.Numeric(2)) { Name = "First" }], null, default));
        Assert.Equal(1, error.Data["UWSCSharp.ComArgumentIndex"]);
        Assert.Equal("Change", error.Data["UWSCSharp.ComMember"]);
    }

    [Fact]
    public async Task ThreadErrorInfoIsReadReleasedAndNotReused()
    {
        using var fixture = new DispatchFixture { HResult = unchecked((int)0x80004005) };
        fixture.Operation = (_, _, _) => { };
        fixture.BeforeReturn = () =>
        {
            Marshal.ThrowExceptionForHR(CreateErrorInfo(out ICreateErrorInfo info));
            try
            {
                Marshal.ThrowExceptionForHR(info.SetSource("thread-source"));
                Marshal.ThrowExceptionForHR(info.SetDescription("スレッド例外"));
                Marshal.ThrowExceptionForHR(info.SetHelpFile("thread-help.chm"));
                Marshal.ThrowExceptionForHR(info.SetHelpContext(99));
                Marshal.ThrowExceptionForHR(SetErrorInfo(0, (ComErrorInfo.IErrorInfo)info));
            }
            finally
            {
                Marshal.ReleaseComObject(info);
            }
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        COMException first = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync(root, "Change", [], null, default));
        Assert.Null(fixture.Failure);
        Assert.Equal("スレッド例外", first.Message);
        Assert.Equal("thread-source", first.Source);
        Assert.Equal("thread-help.chm", first.HelpLink);
        Assert.Equal(99, first.Data["UWSCSharp.ComHelpContext"]);
        fixture.BeforeReturn = null;
        COMException second = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync(root, "Change", [], null, default));
        Assert.NotEqual(first.Message, second.Message);
    }

    [Fact]
    public async Task DeferredFillFailureDoesNotReplaceOriginalInvokeFailure()
    {
        using var fixture = new DispatchFixture { HResult = unchecked((int)0x80020009) };
        fixture.Operation = (_, _, _) => { };
        ComErrorInfo.FillIn callback = (ref System.Runtime.InteropServices.ComTypes.EXCEPINFO info) =>
        {
            info.bstrDescription = "破棄する途中情報";
            info.scode = unchecked((int)0x80040202);
            return unchecked((int)0x80004005);
        };
        fixture.ExceptionInfo = () => new()
        {
            pfnDeferredFillIn = Marshal.GetFunctionPointerForDelegate(callback),
            wCode = 1200
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync(root, "Change", [], null, default));
        Assert.Equal(unchecked((int)0x80020009), error.ErrorCode);
        Assert.NotEqual("破棄する途中情報", error.Message);
        Assert.Equal((ushort)1200, error.Data["UWSCSharp.ComExceptionCode"]);
        Assert.IsAssignableFrom<Exception>(error.Data["UWSCSharp.ComDeferredError"]);
    }

    [Fact]
    public async Task NegativeFractionalDateKeepsNativeNumericRepresentation()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        fixture.Operation = (arguments, _, result) =>
        {
            if (call++ == 0)
            {
                ComVariantValue.Write(new ComVariantValue(7, -0.25d), result);
            }
            else
            {
                Assert.Equal(7, Marshal.ReadInt16(arguments));
                Assert.Equal(BitConverter.DoubleToInt64Bits(-0.25), Marshal.ReadInt64(arguments, 8));
            }
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Value date = await session.InvokeAsync(root, "Change", [], null, default);
        Assert.Equal(Value.Numeric(-0.25), date);
        await session.InvokeAsync(root, "Change", [date], null, default);
        Assert.Null(fixture.Failure);
    }
}
