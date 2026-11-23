using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリ領域だけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllNumericReferenceFieldTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 19)]
    public async Task ScalarAndArrayReferenceKeepTheirOwnTypesAndArgumentOrder(
        bool arrayLast, int expectedTag)
    {
        bool separate = false;
        using var loader = new Loader(root =>
        {
            nint first = Marshal.ReadIntPtr(root + 1);
            nint second = Marshal.ReadIntPtr(root + 10);
            separate = first != second && Marshal.ReadInt32(first) == 11
                && Marshal.ReadInt32(second) == 11;
            Marshal.WriteInt32(first, 101);
            Marshal.WriteInt32(second, 202);
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,{{(arrayLast ? "VAR uint" : "uint[]")}},byte,{{(arrayLast ? "uint[]" : "VAR uint")}}}):int:kernel32.dll PACK
            DIM value=11.25
            PRINT Inspect(7,value,42,value)
            PRINT value
            PRINT VARTYPE(value)
            """, output.Add);

        Assert.True(separate);
        Assert.Equal(["7", "202", expectedTag.ToString()], output);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("boolean")]
    [InlineData("word")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("bool")]
    [InlineData("uint")]
    [InlineData("dword")]
    [InlineData("longlong")]
    [InlineData("float")]
    [InlineData("double")]
    public async Task NullVariableFailsAfterCallingWithNullPointer(string type)
    {
        nint observed = -1;
        using var loader = new Loader(root =>
        {
            observed = Marshal.ReadIntPtr(root + 1);
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,VAR {{type}},byte}):int:kernel32.dll PACK
            DIM value=NULL
            TRY
                Inspect(7,value,42)
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT VARTYPE(value)
            """, output.Add);

        Assert.Equal(["rejected", "1"], output);
        Assert.Equal(0, observed);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("double")]
    public async Task ArrayArgumentIsRejectedBeforeLoadingLibrary(string type)
    {
        using var loader = new Loader(_ => 7);
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,VAR {{type}},byte}):int:kernel32.dll PACK
            DIM values[2]=11,22,33
            TRY
                Inspect(7,values,42)
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PRINT values[0]
            """, output.Add);

        Assert.Equal(["rejected", "11"], output);
        Assert.Equal(0, loader.Loads);
    }

    [Fact]
    public async Task CancellationPublishesNativeWritebackBeforeFinally()
    {
        using var cancelled = new CancellationTokenSource();
        using var loader = new Loader(root =>
        {
            Marshal.WriteInt32(Marshal.ReadIntPtr(root + 1), 101);
            cancelled.Cancel();
            return 7;
        });
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInterpreter(loader).RunAsync("""
                OPTION OPTFINALLY
                DEF_DLL Inspect({byte,VAR int,byte}):int:kernel32.dll PACK
                DIM value=11.25
                TRY
                    Inspect(7,value,42)
                FINALLY
                    PRINT value
                ENDTRY
                """, output.Add, cancelled.Token));

        Assert.Equal(["101"], output);
        Assert.Equal(1, loader.Frees);
    }

    private static Interpreter CreateInterpreter(Loader loader) =>
        new(windows: new WindowsAutomation(_ => { }) { NativeDllLoader = loader });

    private sealed class Loader(Loader.NativeCall callback) : INativeDllLoader, IDisposable
    {
        internal int Loads;
        internal int Frees;

        public nint Load(string path)
        {
            Loads++;
            return 1;
        }

        public nint GetExport(nint library, string export) =>
            Marshal.GetFunctionPointerForDelegate(callback);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(callback);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int NativeCall(nint root);
    }
}
