using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリだけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllStructureLimitTests
{
    [Theory]
    [InlineData(false, false, 7)]
    [InlineData(false, true, 7)]
    [InlineData(true, false, 7)]
    [InlineData(true, true, 7)]
    [InlineData(false, false, 8)]
    [InlineData(false, true, 8)]
    [InlineData(true, false, 8)]
    [InlineData(true, true, 8)]
    public async Task StructureBudgetIncludesParentsAndSiblings(
        bool siblings, bool packed, int count)
    {
        string type = "int";
        if (siblings)
        {
            type = "{" + string.Join(",", Enumerable.Repeat("{int}", count - 1)) + "}";
        }
        else
        {
            for (int index = 0; index < count; index++)
            {
                type = "{" + type + "}";
            }
        }
        int leaves = siblings ? count - 1 : 1;
        string arguments = string.Join(",", Enumerable.Repeat("ReadLeaf()", leaves));
        using var loader = new Loader(_ => 42);
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{type}}):int:kernel32.dll {{(packed ? "PACK" : "")}}
            PUBLIC evaluated=0
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT evaluated
            FUNCTION ReadLeaf()
                evaluated=evaluated+1
                RESULT=11
            FEND
            """, output.Add);

        Assert.Equal([count <= 7 ? "42" : "error", leaves.ToString()], output);
        Assert.Equal(count <= 7 ? 1 : 0, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task StructureBudgetIsSharedAcrossRootParameters(int count)
    {
        string types = string.Join(",", Enumerable.Repeat("{int}", count));
        string arguments = string.Join(",", Enumerable.Repeat("11", count));
        using var loader = new Loader(_ => 42);
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            TRY
                PRINT Inspect({{arguments}})
            EXCEPT
                PRINT "error"
            ENDTRY
            """, output.Add);

        Assert.Equal([count <= 7 ? "42" : "error"], output);
        Assert.Equal(count <= 7 ? 1 : 0, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false, 2048)]
    [InlineData(false, 2049)]
    [InlineData(true, 2048)]
    [InlineData(true, 2049)]
    public async Task ReferencedTextUsesOnlyPointerWidthInStructure(
        bool reference, int length)
    {
        using var loader = new Loader(_ => 42);
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{{(reference ? "VAR " : "")}}string}):int:kernel32.dll
            DIM value="{{new string('a', length)}}"
            TRY
                PRINT Inspect(value)
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT LENGTH(value)
            """, output.Add);

        bool accepted = reference || length <= 2048;
        Assert.Equal([accepted ? "42" : "error", length.ToString()], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteBudgetAppliesToEachStructureSeparately(bool packed)
    {
        using var loader = new Loader(root =>
        {
            nint first = Marshal.ReadIntPtr(root);
            nint second = Marshal.ReadIntPtr(root + nint.Size);
            Marshal.WriteByte(first + 1499, (byte)'Z');
            Marshal.WriteByte(second + 1499, (byte)'Z');
            return 42;
        });
        var output = new List<string>();
        string type = "{{string},{string}}";
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{type}}):int:kernel32.dll {{(packed ? "PACK" : "")}}
            DIM value="{{new string('a', 1500)}}", other=value
            PRINT Inspect(value,other)
            PRINT LENGTH(value)
            PRINT COPY(value,LENGTH(value),1)
            PRINT LENGTH(other)
            PRINT COPY(other,LENGTH(other),1)
            """, output.Add);

        Assert.Equal(["42", "1500", "Z", "1500", "Z"], output);
        Assert.Equal(1, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    private static Interpreter CreateInterpreter(Loader loader) =>
        new(windows: new WindowsAutomation(_ => { }) { NativeDllLoader = loader });

    private sealed class Loader(Loader.NativeCall callback) : INativeDllLoader, IDisposable
    {
        internal int Calls;
        internal int Frees;
        private readonly NativeCall trampoline = root => callback(root);

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export)
        {
            NativeCall call = root =>
            {
                Calls++;
                return trampoline(root);
            };
            retained = call;
            return Marshal.GetFunctionPointerForDelegate(call);
        }

        private NativeCall? retained;

        public void Free(nint library) => Frees++;

        public void Dispose()
        {
            GC.KeepAlive(retained);
            GC.KeepAlive(trampoline);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int NativeCall(nint root);
    }
}
