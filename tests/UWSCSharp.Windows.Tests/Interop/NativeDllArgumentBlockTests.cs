using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリだけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllArgumentBlockTests
{
    [Theory]
    [InlineData("int", 512, true)]
    [InlineData("int", 513, false)]
    [InlineData("float", 512, true)]
    [InlineData("float", 513, false)]
    [InlineData("byte", 512, true)]
    [InlineData("byte", 513, false)]
    [InlineData("word", 512, true)]
    [InlineData("word", 513, false)]
    [InlineData("longlong", 256, true)]
    [InlineData("longlong", 257, false)]
    [InlineData("double", 256, true)]
    [InlineData("double", 257, false)]
    public async Task ScalarCapacityUsesTypeWidth(string type, int count, bool accepted)
    {
        string types = string.Join(",", Enumerable.Repeat(type, count));
        string arguments = string.Join(",", Enumerable.Repeat("11", count));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT POS("2048バイト",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal([accepted ? "42" : "1"], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
        Assert.Equal(accepted ? 1 : 0, loader.Loads);
        Assert.Equal(accepted ? 1 : 0, loader.Frees);
    }

    [Theory]
    [InlineData(false, 512, true)]
    [InlineData(false, 513, false)]
    [InlineData(true, 511, true)]
    [InlineData(true, 512, false)]
    public async Task LastArgumentCanCrossBoundary(bool wide, int count, bool accepted)
    {
        string types = string.Join(",", Enumerable.Repeat("int", count - 1))
            + (wide ? ",double" : ",{int}");
        string arguments = string.Join(",", Enumerable.Repeat("11", count));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT POS("2048バイト",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal([accepted ? "42" : "1"], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
        Assert.Equal(accepted ? 1 : 0, loader.Loads);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task FieldsOnlyUseOneOuterSlot(int count, bool accepted)
    {
        string type = "{" + string.Join(",", Enumerable.Repeat("int", count)) + "}";
        string arguments = string.Join(",", Enumerable.Repeat("11", count));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{type}}):int:kernel32.dll
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT POS("構造体の大きさ",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal([accepted ? "42" : "1"], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
        Assert.Equal(1, loader.Loads);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task NullArrayPointersUseFourBytesInCapacity(int count, bool accepted)
    {
        string types = string.Join(",", Enumerable.Repeat("double[]", count));
        string arguments = string.Join(",", Enumerable.Repeat("NULL", count));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT POS("2048バイト",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal([accepted ? "42" : "1"], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
    }

    private static Interpreter CreateInterpreter(Loader loader) =>
        new(windows: new WindowsAutomation(_ => { }) { NativeDllLoader = loader });

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeCall callback;
        internal int Loads;
        internal int Calls;
        internal int Frees;

        internal Loader()
        {
            callback = () =>
            {
                Calls++;
                return 42;
            };
        }

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
        private delegate int NativeCall();
    }
}
