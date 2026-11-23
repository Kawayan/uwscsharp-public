using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリ領域だけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllArrayFieldTests
{
    [Fact]
    public async Task PackedArrayPointersRemainSeparateBesideAlignedChildPointer()
    {
        bool separate = false;
        using var loader = new Loader(root =>
        {
            nint first = Marshal.ReadIntPtr(root + 1);
            nint child = Marshal.ReadIntPtr(root + 16);
            nint numbers = Marshal.ReadIntPtr(child + 1);
            nint last = Marshal.ReadIntPtr(root + 24);
            separate = first != last && Marshal.ReadByte(root + 9) == 42;
            Marshal.WriteInt32(first, 101);
            Marshal.WriteInt32(last, 201);
            Marshal.WriteInt64(numbers, BitConverter.DoubleToInt64Bits(7.25));
            Marshal.WriteIntPtr(root + 24, first);
            Marshal.WriteIntPtr(root + 16, 1);
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync("""
            DEF_DLL Inspect({byte,int[],byte,{byte,VAR double[]},VAR int[]}):int:kernel32.dll PACK
            DIM shared[2]=11,22,33
            DIM numbers[2]=1,2,3
            PRINT Inspect(7,shared,42,8,numbers,shared)
            PRINT shared[0]
            PRINT shared[1]
            PRINT numbers[0]
            PRINT LENGTH(numbers)
            """, output.Add);

        Assert.True(separate);
        Assert.Equal(["7", "201", "22", "7.25", "3"], output);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task PartialArrayWriteBackReevaluatesItsRowIndex()
    {
        using var loader = new Loader(root =>
        {
            Marshal.WriteInt32(Marshal.ReadIntPtr(root + 1), 101);
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync("""
            DEF_DLL Inspect({byte,int[],byte,double}):int:kernel32.dll PACK
            PUBLIC lookups=0
            DIM matrix[1,2]=11,22,33,44,55,66
            PRINT Inspect(7,matrix[ChooseRow()],42,1.25)
            PRINT matrix[0,0]
            PRINT matrix[1,0]
            PRINT matrix[1,2]
            PRINT lookups
            FUNCTION ChooseRow()
                lookups=lookups+1
                RESULT=lookups-1
            FEND
            """, output.Add);

        Assert.Equal(["7", "11", "101", "33", "2"], output);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task EmptyArrayIsRejectedBeforeLoadingLibrary()
    {
        using var loader = new Loader(_ => 7);
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync("""
            DEF_DLL Inspect({byte,int[],byte}):int:kernel32.dll
            DIM values[-1]
            TRY
                Inspect(7,values,42)
            EXCEPT
                PRINT LENGTH(values)
                PRINT POS("要素のある数値配列",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal(["0", "1"], output);
        Assert.Equal(0, loader.Loads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntArrayFieldRequiresNullLiteral(bool reference)
    {
        bool nullPointer = false;
        using var loader = new Loader(root =>
        {
            nullPointer = Marshal.ReadIntPtr(root + 1) == 0
                && Marshal.ReadByte(root + 1 + nint.Size) == 42;
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,{{(reference ? "VAR " : "")}}int[],byte}):int:kernel32.dll PACK
            DIM value=NULL
            TRY
                Inspect(7,value,42)
            EXCEPT
                PRINT "null-rejected"
            ENDTRY
            PRINT Inspect(7,NULL,42)
            PRINT VARTYPE(value)
            """, output.Add);

        Assert.Equal(["null-rejected", "7", "1"], output);
        Assert.True(nullPointer);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData("uint", false, 19, 0)]
    [InlineData("uint", true, 19, 0)]
    [InlineData("dword", false, 19, 0)]
    [InlineData("dword", true, 19, 0)]
    [InlineData("int", false, 3, 11)]
    [InlineData("double", true, 5, 11.25)]
    public async Task ScalarArrayFieldUsesOneElementAndPreservesWriteBackType(
        string type, bool reference, int tag, double expectedBefore)
    {
        double before = -1;
        using var loader = new Loader(root =>
        {
            nint value = Marshal.ReadIntPtr(root + 1);
            before = type == "double"
                ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(value))
                : Marshal.ReadInt32(value);
            if (type == "double")
            {
                Marshal.WriteInt64(value, BitConverter.DoubleToInt64Bits(101));
            }
            else
            {
                Marshal.WriteInt32(value, 101);
            }
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,{{(reference ? "VAR " : "")}}{{type}}[],byte}):int:kernel32.dll PACK
            DIM value={{(type is "uint" or "dword" ? "NULL" : "11.25")}}
            PRINT Inspect(7,value,42)
            PRINT VARTYPE(value)
            PRINT value
            """, output.Add);

        Assert.Equal(expectedBefore, before);
        Assert.Equal(["7", tag.ToString(), "101"], output);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData("uint", false)]
    [InlineData("uint", true)]
    [InlineData("dword", false)]
    [InlineData("dword", true)]
    public async Task UnsignedArrayFieldsPreserveElementType(string type, bool reference)
    {
        using var loader = new Loader(root =>
        {
            Marshal.WriteInt32(Marshal.ReadIntPtr(root + 1), 101);
            return 7;
        });
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({byte,{{(reference ? "VAR " : "")}}{{type}}[],byte}):int:kernel32.dll PACK
            DIM values[2]=11,22,33
            PRINT Inspect(7,values,42)
            PRINT values[0]
            PRINT VARTYPE(values[0])
            PRINT values[1]
            PRINT VARTYPE(values[1])
            """, output.Add);

        Assert.Equal(["7", "101", "19", "22", "19"], output);
        Assert.Equal(1, loader.Frees);
    }

    [Fact]
    public async Task CancellationFinishesNativeWriteback()
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
                DEF_DLL Inspect({byte,int[],byte}):int:kernel32.dll PACK
                DIM values[2]=11,22,33
                TRY
                    Inspect(7,values,42)
                FINALLY
                    PRINT values[0]
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
