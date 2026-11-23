using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task SuppressedNextFailureDoesNotPublishCollectedItems()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        int next = 0;
        sequence.Operation = cell =>
        {
            if (++next == 1)
            {
                ComVariantValue.Write(42, cell);
                return (0, 1);
            }

            return (unchecked((int)0x80004005), 0);
        };
        List<string> output = [];
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: new Runtime(fixture));
        await new Interpreter(windows: automation).RunAsync("""
            source = CREATEOLEOBJ("fixture")
            COM_ERR_IGN
            FOR item IN source
                PRINT item
            NEXT
            PRINT COM_ERR_FLG
            COM_ERR_RET
            """, output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(2, sequence.Calls);
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedEnumerationArrayPreservesPrimaryAndReleasesAcquisitions(bool failedNext)
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        nint locked = 0;
        fixture.Operation = (_, _, result) => sequence.Return(result);
        sequence.Operation = cell =>
        {
            object instance = Marshal.GetObjectForIUnknown(fixture.Pointer);
            try
            {
                ComVariantValue.Write(new ComArrayValue(12, [0], [0],
                    [new ComVariantValue(9, instance)]), cell);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }

            locked = Marshal.ReadIntPtr(cell, 8);
            Marshal.ThrowExceptionForHR(SafeArrayLock(locked));
            return (failedNext ? unchecked((int)0x80004005) : 0, 1);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        try
        {
            COMException failure = await Assert.ThrowsAsync<COMException>(async () =>
                await session.EnumerateAsync(root, default));
            Assert.Equal(unchecked((int)(failedNext ? 0x80004005u : 0x8002000du)), failure.HResult);
            Assert.Equal(failedNext ? failure.Message : null,
                session.GetScriptErrorMessage(failure));
            if (failedNext)
            {
                COMException cleanup = Assert.IsType<COMException>(
                    failure.Data["UWSCSharp.ComEnumerationCleanupErrors"]);
                Assert.Equal(unchecked((int)0x8002000d), cleanup.HResult);
            }
        }
        finally
        {
            fixture.MemberId = 1;
            fixture.Operation = (_, _, _) =>
            {
                if (locked != 0)
                {
                    Marshal.ThrowExceptionForHR(SafeArrayUnlock(locked));
                    Marshal.ThrowExceptionForHR(SafeArrayDestroy(locked));
                    locked = 0;
                }
            };
            await session.InvokeAsync(root, "Change", [], null, default);
        }

        Assert.Equal(1, sequence.Calls);
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    public async Task LockedMemberResultReleasesReadObjectAcquisitions()
    {
        using var fixture = new DispatchFixture();
        nint locked = 0;
        fixture.Operation = (_, _, result) =>
        {
            object instance = Marshal.GetObjectForIUnknown(fixture.Pointer);
            try
            {
                ComVariantValue.Write(new ComArrayValue(12, [0], [0],
                    [new ComVariantValue(9, instance)]), result);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }

            locked = Marshal.ReadIntPtr(result, 8);
            Marshal.ThrowExceptionForHR(SafeArrayLock(locked));
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(async () =>
                await session.InvokeAsync(root, "Change", [], null, default));
            COMException cleanup = Assert.IsType<COMException>(Assert.Single(failure.InnerExceptions));
            Assert.Equal(unchecked((int)0x8002000d), cleanup.HResult);
        }
        finally
        {
            fixture.Operation = (_, _, _) =>
            {
                if (locked != 0)
                {
                    Marshal.ThrowExceptionForHR(SafeArrayUnlock(locked));
                    Marshal.ThrowExceptionForHR(SafeArrayDestroy(locked));
                    locked = 0;
                }
            };
            await session.InvokeAsync(root, "Change", [], null, default);
        }

        Assert.Null(fixture.Failure);
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayLock(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnlock(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint array);

    [Fact]
    public async Task FailedNestedVariantReadReleasesEarlierObjectAcquisition()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        sequence.Operation = cell =>
        {
            object instance = Marshal.GetObjectForIUnknown(fixture.Pointer);
            try
            {
                ComVariantValue.Write(new ComArrayValue(12, [0], [1],
                    [new ComVariantValue(9, instance), new ComVariantValue(7, 1d)]), cell);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }

            nint array = Marshal.ReadIntPtr(cell, 8);
            Marshal.ThrowExceptionForHR(SafeArrayAccessData(array, out nint data));
            try
            {
                int stride = IntPtr.Size == 8 ? 24 : 16;
                Marshal.WriteInt64(data, stride + 8, BitConverter.DoubleToInt64Bits(double.NaN));
            }
            finally
            {
                Marshal.ThrowExceptionForHR(SafeArrayUnaccessData(array));
            }

            return (0, 1);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.EnumerateAsync(root, default));
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(nint array, out nint data);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(nint array);

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task CumulativeReadLimitReleasesEarlierObjectAcquisition()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        sequence.Operation = cell =>
        {
            object instance = Marshal.GetObjectForIUnknown(fixture.Pointer);
            try
            {
                object[] values = new object[1001];
                values[0] = instance;
                int[] child = new int[1000];
                Array.Fill(values, child, 1, 1000);
                ComVariantValue.Write(values, cell);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }

            return (0, 1);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.EnumerateAsync(root, default));
        Assert.Equal(1, sequence.Calls);
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    private sealed class EnumFixture : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Query(nint self, ref Guid id, out nint result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint Reference(nint self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Next(nint self, uint count, nint value, out uint fetched);

        internal readonly nint Pointer;
        private readonly nint table;
        private readonly Delegate[] callbacks;
        private int references = 1;
        internal Func<nint, (int HResult, uint Fetched)> Operation = _ => (1, 0);
        internal Exception? Failure;
        internal int ThreadId;
        internal int Calls;

        internal EnumFixture()
        {
            callbacks = [new Query(QueryInterface), new Reference(AddRef),
                new Reference(Release), new Next(ReadNext)];
            table = Marshal.AllocCoTaskMem(callbacks.Length * IntPtr.Size);
            for (int i = 0; i < callbacks.Length; i++)
            {
                Marshal.WriteIntPtr(table, i * IntPtr.Size,
                    Marshal.GetFunctionPointerForDelegate(callbacks[i]));
            }

            Pointer = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, table);
        }

        private int QueryInterface(nint self, ref Guid id, out nint result)
        {
            if (id == new Guid("00000000-0000-0000-C000-000000000046")
                || id == new Guid("00020404-0000-0000-C000-000000000046"))
            {
                result = self;
                AddRef(self);
                return 0;
            }

            result = 0;
            return unchecked((int)0x80004002);
        }

        private uint AddRef(nint self) => (uint)Interlocked.Increment(ref references);

        private uint Release(nint self) => (uint)Interlocked.Decrement(ref references);

        private int ReadNext(nint self, uint count, nint value, out uint fetched)
        {
            fetched = 0;
            try
            {
                Assert.Equal(1u, count);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                ThreadId = Environment.CurrentManagedThreadId;
                Calls++;
                (int hr, uint returned) = Operation(value);
                fetched = returned;
                return hr;
            }
            catch (Exception ex)
            {
                Failure = ex;
                return unchecked((int)0x80004005);
            }
        }

        internal void Return(nint result)
        {
            Marshal.AddRef(Pointer);
            Marshal.WriteIntPtr(result, 8, Pointer);
            Marshal.WriteInt16(result, 13);
        }

        public void Dispose()
        {
            Assert.Equal(1, references);
            Release(Pointer);
            Marshal.FreeCoTaskMem(Pointer);
            Marshal.FreeCoTaskMem(table);
            GC.KeepAlive(callbacks);
        }
    }

    [Fact]
    public async Task NativeEnumerationRetainsScalarArrayAndObjectTags()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        int index = 0;
        sequence.Operation = cell =>
        {
            switch (index++)
            {
                case 0:
                    break;
                case 1:
                    ComVariantValue.Write(DBNull.Value, cell);
                    break;
                case 2:
                    ComVariantValue.Write(new DispatchWrapper(null), cell);
                    break;
                case 3:
                    ComVariantValue.Write(new ErrorWrapper(-42), cell);
                    break;
                case 4:
                    ComVariantValue.Write(new ComVariantValue(7, -0.25d), cell);
                    break;
                case 5:
                    ComVariantValue.Write(new ComArrayValue(3, [-2], [-2], [9d]), cell);
                    break;
                case 6:
                    Marshal.AddRef(fixture.Pointer);
                    Marshal.WriteIntPtr(cell, 8, fixture.Pointer);
                    Marshal.WriteInt16(cell, 9);
                    break;
                default:
                    return (1, 0);
            }

            return (0, 1);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        IReadOnlyList<Value> values = await session.EnumerateAsync(root, default);
        Assert.Equal([ValueKind.Empty, ValueKind.Null, ValueKind.Nothing,
            ValueKind.Error, ValueKind.Number, ValueKind.Array, ValueKind.Object],
            values.Select(value => value.Kind));
        Assert.Equal(-0.25d, values[4].Number);
        Assert.Equal(Value.Numeric(9), values[5].AsArray().Get(-2));
        Assert.Equal(root, values[6]);
        Assert.Equal(fixture.InvokeThread, sequence.ThreadId);
        Assert.Equal(8, sequence.Calls);
        fixture.MemberId = 1;
        fixture.Operation = (arguments, _, _) =>
        {
            Assert.Equal(7, Marshal.ReadInt16(arguments));
            Assert.Equal(-0.25d, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(arguments, 8)));
        };
        await session.InvokeAsync(root, "Change", [values[4]], null, default);
        Assert.Null(fixture.Failure);
        Assert.Null(sequence.Failure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    [InlineData(2, 1)]
    [InlineData(unchecked((int)0x80004005), 1)]
    public async Task InvalidEnumerationRepliesReleaseReturnedNativeReferences(int hr, uint fetched)
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        sequence.Operation = cell =>
        {
            Marshal.AddRef(fixture.Pointer);
            Marshal.WriteIntPtr(cell, 8, fixture.Pointer);
            Marshal.WriteInt16(cell, 9);
            return (hr, fetched);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Exception? failure = await Record.ExceptionAsync(async () =>
            await session.EnumerateAsync(root, default));
        Assert.NotNull(failure);
        Assert.IsType(hr < 0 ? typeof(COMException) : typeof(InvalidOperationException), failure);
        Assert.Equal(failure.Message, session.GetScriptErrorMessage(failure));
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    public async Task CancellationAfterNextReleasesUnpublishedObject()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        using var cancellation = new CancellationTokenSource();
        fixture.Operation = (_, _, result) => sequence.Return(result);
        sequence.Operation = cell =>
        {
            Marshal.AddRef(fixture.Pointer);
            Marshal.WriteIntPtr(cell, 8, fixture.Pointer);
            Marshal.WriteInt16(cell, 9);
            cancellation.Cancel();
            return (0, 1);
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.EnumerateAsync(root, cancellation.Token));
        Assert.Equal(1, sequence.Calls);
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }
}
