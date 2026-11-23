using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリだけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllArgumentLimitTests
{
    public static IEnumerable<object[]> Positions()
    {
        foreach (string kind in new[] { "reference", "array", "text", "inline" })
        {
            foreach (string shape in new[] { "direct", "field", "tail" })
            {
                if (kind == "inline" && shape != "field")
                {
                    continue;
                }
                int limit = kind == "array" ? 20 : 40;
                yield return [kind, shape, limit];
                yield return [kind, shape, limit + 1];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public async Task StorageLimitUsesFlattenedPosition(string kind, string shape, int count)
    {
        int limit = kind == "array" ? 20 : 40;
        string scalar = kind switch
        {
            "array" => "int[]",
            "text" => "VAR string",
            "inline" => "string",
            _ => "VAR int"
        };
        string[] types = [.. Enumerable.Repeat(shape == "tail" ? "int" : scalar, count)];
        types[^1] = scalar;
        string[] arguments = [.. Enumerable.Range(0, count).Select(index => $"values[{index}]")];
        string setup = $"DIM values[{count - 1}]="
            + string.Join(",", Enumerable.Repeat(kind is "text" or "inline" ? "\"abc\"" : "11", count));
        if (kind == "array")
        {
            setup = $"DIM values[{count - 1},0]="
                + string.Join(",", Enumerable.Repeat("11", count));
            if (shape == "tail")
            {
                for (int index = 0; index < count - 1; index++)
                {
                    arguments[index] = $"values[{index},0]";
                }
            }
        }
        else if (kind == "text" && shape == "tail")
        {
            setup = $"DIM values[{count - 1}]="
                + string.Join(",", Enumerable.Repeat("11", count)) + "\nDIM label=\"abc\"";
            arguments[^1] = "label";
        }
        string declaration = string.Join(",", types);
        if (shape == "field")
        {
            declaration = "{" + declaration + "}";
        }
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{declaration}}):int:kernel32.dll
            {{setup}}
            TRY
                PRINT Inspect({{string.Join(",", arguments)}})
            EXCEPT
                PRINT "error"
            ENDTRY
            """, output.Add);

        Assert.Equal([count <= limit ? "42" : "error"], output);
        Assert.Equal(count <= limit ? 1 : 0, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainNumbersCanPassFortyOnePositions(bool fields)
    {
        string types = string.Join(",", Enumerable.Repeat("int", 41));
        if (fields)
        {
            types = "{" + types + "}";
        }
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            PRINT Inspect({{string.Join(",", Enumerable.Repeat("11", 41))}})
            """, output.Add);

        Assert.Equal(["42"], output);
        Assert.Equal(1, loader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedFieldPositionsFollowDeclarationOrder(bool childFirst)
    {
        string plain = string.Join(",", Enumerable.Repeat("int", 40));
        string type = childFirst ? "{{VAR int}," + plain + "}" : "{" + plain + ",{VAR int}}";
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{type}}):int:kernel32.dll
            TRY
                PRINT Inspect({{string.Join(",", Enumerable.Repeat("11", 41))}})
            EXCEPT
                PRINT "error"
            ENDTRY
            """, output.Add);

        Assert.Equal([childFirst ? "42" : "error"], output);
        Assert.Equal(childFirst ? 1 : 0, loader.Calls);
    }

    [Fact]
    public async Task NullArrayDoesNotAllocateStorageAtTwentyFirstPosition()
    {
        string prefix = string.Join(",", Enumerable.Repeat("int", 20));
        string arguments = string.Join(",", Enumerable.Repeat("11", 20));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{prefix}},int[]):int:kernel32.dll
            PRINT Inspect({{arguments}},NULL)
            """, output.Add);

        Assert.Equal(["42"], output);
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public async Task FailedArrayPositionDoesNotPublishScalarConversion()
    {
        string type = "{" + string.Join(",", Enumerable.Repeat("int", 20)) + ",uint[]}";
        string arguments = string.Join(",", Enumerable.Repeat("11", 20)) + ",value";
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{type}}):int:kernel32.dll
            DIM value=2.25
            TRY
                Inspect({{arguments}})
            EXCEPT
                PRINT POS("20番目",TRY_ERRMSG)>0
            ENDTRY
            PRINT value
            PRINT VARTYPE(value)
            """, output.Add);

        Assert.Equal(["1", "2.25", "5"], output);
        Assert.Equal(0, loader.Calls);
        Assert.Equal(1, loader.Frees);
    }

    public static IEnumerable<object[]> NullTextPositions()
    {
        foreach (string type in new[] { "string", "pchar", "pbyte", "wstring", "pwchar" })
        {
            foreach (bool fields in new[] { false, true })
            {
                foreach (bool variable in new[] { false, true })
                {
                    yield return [type, fields, variable, 40];
                    yield return [type, fields, variable, 41];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(NullTextPositions))]
    public async Task NullTextStillChecksStoragePosition(string primitive,
        bool fields, bool variable, int count)
    {
        string types = string.Join(",", Enumerable.Repeat("VAR " + primitive, count));
        if (fields)
        {
            types = "{" + types + "}";
        }

        string[] arguments = [.. Enumerable.Range(0, count)
            .Select(index => $"values[{index}]")];
        arguments[^1] = variable ? "nullValue" : "NULL";
        string values = string.Join(",", Enumerable.Repeat("\"abc\"", count));
        using var loader = new Loader();
        var output = new List<string>();
        await CreateInterpreter(loader).RunAsync($$"""
            DEF_DLL Inspect({{types}}):int:kernel32.dll
            DIM values[{{count - 1}}]={{values}}
            DIM nullValue=NULL
            TRY
                PRINT Inspect({{string.Join(",", arguments)}})
            EXCEPT
                PRINT POS("40番目",TRY_ERRMSG)>0
            ENDTRY
            PRINT VARTYPE(nullValue)
            """, output.Add);

        bool accepted = count == 40;
        int tag = accepted && variable
            ? primitive is "wstring" or "pwchar" ? 258 : 256 : 1;
        Assert.Equal([accepted ? "42" : "1", tag.ToString()], output);
        Assert.Equal(accepted ? 1 : 0, loader.Calls);
    }

    private static Interpreter CreateInterpreter(Loader loader) =>
        new(windows: new WindowsAutomation(_ => { }) { NativeDllLoader = loader });

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeCall callback;
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

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) =>
            Marshal.GetFunctionPointerForDelegate(callback);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(callback);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NativeCall();
    }
}
