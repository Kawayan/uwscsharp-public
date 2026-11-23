using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリ領域だけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllTextFieldTests
{
    [Fact]
    public async Task PackedFieldsKeepSeparateBuffersAndReadChangedTextPointer()
    {
        bool separated = false;
        using var loader = new Loader(root =>
        {
            nint first = Marshal.ReadIntPtr(root + 1);
            nint child = Marshal.ReadIntPtr(root + 16);
            nint wide = Marshal.ReadIntPtr(child + 1);
            nint last = Marshal.ReadIntPtr(root + 24);
            separated = first != last && Marshal.ReadByte(root + 9) == 42
                && Marshal.ReadByte(child + 9) == 43;
            Marshal.WriteByte(first, (byte)'X');
            Marshal.WriteByte(first + 1, 0);
            Marshal.WriteByte(first + 2, (byte)'Z');
            Marshal.WriteInt16(wide, 0x65e5);
            Marshal.WriteInt16(wide + 2, 0);
            Marshal.WriteIntPtr(root + 24, first + 2);
            Marshal.WriteIntPtr(root + 16, 1);
            return 7;
        });
        var output = new List<string>();
        var automation = new WindowsAutomation(_ => { }) { NativeDllLoader = loader };
        await new Interpreter(windows: automation).RunAsync("""
            DEF_DLL Inspect({byte,VAR pchar,byte,{byte,VAR wstring,byte},VAR string}):int:kernel32.dll PACK
            DIM shared="abcde", wide="12345", tail=42, childTail=43
            PRINT Inspect(7,shared,tail,8,wide,childTail,shared)
            PRINT shared
            PRINT wide
            PRINT tail+childTail
            """, output.Add);

        Assert.True(separated);
        Assert.Equal(["7", "Zde", "日", "85"], output);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("wstring")]
    public async Task UnreadableReplacementReportsScriptErrorWithoutWritingBack(string type)
    {
        using var loader = new Loader(root =>
        {
            Marshal.WriteByte(root, 99);
            Marshal.WriteIntPtr(root + nint.Size, 1);
            return 7;
        });
        var output = new List<string>();
        var automation = new WindowsAutomation(_ => { }) { NativeDllLoader = loader };
        await new Interpreter(windows: automation).RunAsync($$"""
            DEF_DLL Inspect({byte,VAR {{type}}}):int:kernel32.dll
            DIM tag=7, value="abcde"
            TRY
                Inspect(tag,value)
            EXCEPT
                PRINT tag
                PRINT value
                PRINT POS("読み取れません",TRY_ERRMSG)>0
            ENDTRY
            """, output.Add);

        Assert.Equal(["7", "abcde", "1"], output);
        Assert.Equal(1, loader.Frees);
    }

    private sealed class Loader(Loader.NativeCall callback) : INativeDllLoader, IDisposable
    {
        internal int Frees;

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) =>
            Marshal.GetFunctionPointerForDelegate(callback);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(callback);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int NativeCall(nint root);
    }
}
