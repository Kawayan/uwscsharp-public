using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllCallSiteTests
{
    [Fact]
    public void MixedTypesKeepRegisterAndStackPositions()
    {
        Mixed callback = (a, b, c, d, e, f) => a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f;
        var call = new NativeDllCallSite(typeof(double),
            typeof(int), typeof(double), typeof(int), typeof(float), typeof(int), typeof(double));
        object? result = call.Invoke(Marshal.GetFunctionPointerForDelegate(callback),
            1, 2.5d, 3, 4.5f, 5, 6.25d);
        Assert.Equal(95.5d, Assert.IsType<double>(result));
        GC.KeepAlive(callback);
    }

    [Fact]
    public void MoreThanSixteenArgumentsDoNotRequireGenericDelegateArity()
    {
        int[]? received = null;
        int? calculated = null;
        int callbackThread = 0;
        int calls = 0;
        Many callback = (a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q) =>
        {
            Interlocked.Increment(ref calls);
            received = [a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q];
            callbackThread = Environment.CurrentManagedThreadId;
            int value = a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h
                + 9 * i + 10 * j + 11 * k + 12 * l + 13 * m + 14 * n + 15 * o + 16 * p + 17 * q;
            // 逆P/Invokeの返却前とcalli後の値を、失敗時に区別できるよう保持します。
            calculated = value;
            return value;
        };
        var call = new NativeDllCallSite(typeof(int), [.. Enumerable.Repeat(typeof(int), 17)]);
        int[] expectedArguments = [.. Enumerable.Range(1, 17)];
        nint address = Marshal.GetFunctionPointerForDelegate(callback);
        int callerThread = Environment.CurrentManagedThreadId;
        int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        try
        {
            object? result = call.Invoke(address, [.. expectedArguments.Cast<object?>()]);
            int[] collectionsAfter = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            if (calls != 1 || received is null || !received.SequenceEqual(expectedArguments)
                || calculated != 1785 || result is not int actual || actual != 1785)
            {
                Assert.Fail($"17引数の呼出し結果が一致しません。expected=1785; "
                    + $"received=[{string.Join(",", received ?? [])}]; "
                    + $"calculated={calculated}; result={result}; type={result?.GetType().FullName}; "
                    + $"calls={calls}; callerThread={callerThread}; callbackThread={callbackThread}; "
                    + $"address=0x{address:x}; runtime={Environment.Version}; "
                    + $"gcBefore=[{string.Join(",", collectionsBefore)}]; "
                    + $"gcAfter=[{string.Join(",", collectionsAfter)}]");
            }
        }
        finally
        {
            GC.KeepAlive(callback);
        }
    }

    [Fact]
    public void InvalidArgumentsAreRejectedBeforeEnteringNativeCode()
    {
        int calls = 0;
        Unary callback = value => { calls++; return value; };
        nint address = Marshal.GetFunctionPointerForDelegate(callback);
        var call = new NativeDllCallSite(typeof(int), typeof(int));
        Assert.Throws<ArgumentException>(() => call.Invoke(address));
        Assert.Throws<ArgumentException>(() => call.Invoke(address, 1, 2));
        Assert.Throws<ArgumentException>(() => call.Invoke(address, 1L));
        Assert.Throws<ArgumentException>(() => call.Invoke(address, (object?)null));
        Assert.Throws<ArgumentNullException>(() => call.Invoke(address, null!));
        Assert.Throws<ArgumentException>(() => call.Invoke(0, 1));
        Assert.Equal(0, calls);
        GC.KeepAlive(callback);
    }

    [Theory]
    [InlineData(typeof(bool))]
    [InlineData(typeof(char))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(int?))]
    public void ImplicitMarshallingTypesAreRejected(Type type)
    {
        Assert.Throws<ArgumentException>(() => new NativeDllCallSite(type));
        Assert.Throws<ArgumentException>(() => new NativeDllCallSite(typeof(void), type));
    }

    [Fact]
    public void ByReferenceAndVoidParametersRequireExplicitBuffers()
    {
        Assert.Throws<ArgumentException>(() => new NativeDllCallSite(typeof(void), typeof(int).MakeByRefType()));
        Assert.Throws<ArgumentException>(() => new NativeDllCallSite(typeof(void), typeof(void)));
        Assert.Throws<ArgumentException>(() => new NativeDllCallSite(typeof(void), [null!]));
    }

    [Fact]
    public void VoidReturnAndPointerParameterWriteCallerStorage()
    {
        Writer callback = pointer => Marshal.WriteInt32(pointer, 123);
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 0);
            var call = new NativeDllCallSite(typeof(void), typeof(nint));
            Assert.Null(call.Invoke(Marshal.GetFunctionPointerForDelegate(callback), buffer));
            Assert.Equal(123, Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            GC.KeepAlive(callback);
        }
    }

    [Fact]
    public void SignatureIsIndependentOfCallerArray()
    {
        Unary callback = value => value + 1;
        Type[] types = [typeof(int)];
        var call = new NativeDllCallSite(typeof(int), types);
        types[0] = typeof(long);
        Assert.Equal(4, Assert.IsType<int>(call.Invoke(Marshal.GetFunctionPointerForDelegate(callback), 3)));
        GC.KeepAlive(callback);
    }

    [Fact]
    public void SharedSignatureDoesNotCaptureAnExportAddress()
    {
        Unary first = value => value + 1;
        Unary second = value => value - 1;
        var call = new NativeDllCallSite(typeof(int), typeof(int));
        Assert.Equal(11, Assert.IsType<int>(call.Invoke(Marshal.GetFunctionPointerForDelegate(first), 10)));
        Assert.Equal(9, Assert.IsType<int>(call.Invoke(Marshal.GetFunctionPointerForDelegate(second), 10)));
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    [Fact]
    public async Task CallSiteCanBeSharedByConcurrentCalls()
    {
        Unary callback = value => value * 2;
        nint address = Marshal.GetFunctionPointerForDelegate(callback);
        var call = new NativeDllCallSite(typeof(int), typeof(int));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(value => Task.Run(() =>
            Assert.Equal(value * 2, Assert.IsType<int>(call.Invoke(address, value))))));
        GC.KeepAlive(callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate double Mixed(int a, double b, int c, float d, int e, double f);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Many(int a, int b, int c, int d, int e, int f, int g, int h,
        int i, int j, int k, int l, int m, int n, int o, int p, int q);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Unary(int value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Writer(nint pointer);
}
