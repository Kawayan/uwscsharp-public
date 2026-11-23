using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 所有するcallbackとメモリだけを使い、画面や入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllNullReferenceTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string type in new[]
        {
            "byte", "boolean", "word", "int", "long", "bool",
            "uint", "dword", "longlong", "float", "double"
        })
        {
            string[] sources = type == "int"
                ? ["literal", "variable", "expression", "element", "constant"]
                : ["literal", "variable", "expression"];
            foreach (string source in sources)
            {
                for (int position = 0; position < 3; position++)
                {
                    yield return [type, source, position, false];
                    yield return [type, source, position, true];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NullReferenceKeepsCallAndPartialWritebackOrder(string type,
        string source, int position, bool fields)
    {
        NativeDllPrimitive primitive = Enum.Parse<NativeDllPrimitive>(type, ignoreCase: true);
        using var loader = new Loader(primitive, fields);
        string types = string.Join(",", Enumerable.Repeat("VAR " + type, 3));
        if (fields)
        {
            types = "{" + types + "}";
        }
        string[] arguments = ["first", "second", "third"];
        arguments[position] = source switch
        {
            "variable" => "nullValue",
            "expression" => "ReturnNullValue()",
            "element" => "nullArray[0]",
            "constant" => "nullConstant",
            _ => "NULL"
        };
        var output = new List<string>();
        var interpreter = new Interpreter(windows:
            new WindowsAutomation(_ => { }) { NativeDllLoader = loader });
        await interpreter.RunAsync($$"""
            DEF_DLL Inspect({{types}},int):int:kernel32.dll {{(fields ? "PACK" : "")}}
            DIM first=1,second=2,third=3,nullValue=NULL
            DIM nullArray[0]=NULL
            CONST nullConstant=NULL
            PUBLIC evaluations=0
            TRY
                PRINT "call="+Inspect({{string.Join(",", arguments)}},{{1 << position}})
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT "values="+first+","+second+","+third
            PRINT "types="+VARTYPE(first)+","+VARTYPE(second)+","+VARTYPE(third)
            PRINT "nulls="+VARTYPE(nullValue)+","+VARTYPE(nullArray[0])
            PRINT "evaluations="+evaluations
            FUNCTION ReturnNullValue()
                evaluations=evaluations+1
                RESULT=NULL
            FEND
            """, output.Add);

        int tag = type switch
        {
            "byte" or "boolean" => 17,
            "word" => 18,
            "longlong" => 20,
            "int" or "long" or "bool" => 3,
            _ => 5
        };
        bool literal = source == "literal";
        int[] values = [1, 2, 3];
        int[] tags = [5, 5, 5];
        for (int index = 0; index < 3; index++)
        {
            if (index != position && (literal || index < position))
            {
                values[index] += 100;
                tags[index] = tag;
            }
        }
        Assert.Equal([literal ? "call=42" : "error",
            "values=" + string.Join(",", values),
            "types=" + string.Join(",", tags), "nulls=1,1",
            "evaluations=" + (source == "expression" ? 1 : 0)], output);
        Assert.Equal(1, loader.Calls);
        Assert.True(loader.Matched);
        Assert.Equal(1, loader.Frees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterArgumentsAreEvaluatedBeforeNullWritebackFails(bool fields)
    {
        using var loader = new Loader(NativeDllPrimitive.Int, fields);
        string types = fields ? "{VAR int,VAR int,VAR int}" : "VAR int,VAR int,VAR int";
        var output = new List<string>();
        var interpreter = new Interpreter(windows:
            new WindowsAutomation(_ => { }) { NativeDllLoader = loader });
        await interpreter.RunAsync($$"""
            DEF_DLL Inspect({{types}},int):int:kernel32.dll
            PUBLIC trace=""
            DIM first=1
            TRY
                Inspect(first,ReturnNullValue(),ReturnThirdValue(),2)
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT trace
            PRINT first
            FUNCTION ReturnNullValue()
                trace=trace+"N"
                RESULT=NULL
            FEND
            FUNCTION ReturnThirdValue()
                trace=trace+"L"
                RESULT=3
            FEND
            """, output.Add);

        Assert.Equal(["error", "NL", "101"], output);
        Assert.Equal(1, loader.Calls);
        Assert.True(loader.Matched);
        Assert.Equal(1, loader.Frees);
    }

    private sealed class Loader : INativeDllLoader, IDisposable
    {
        private readonly NativeDllPrimitive primitive;
        private readonly Delegate callback;
        internal int Calls;
        internal int Frees;
        internal bool Matched = true;

        internal Loader(NativeDllPrimitive primitive, bool fields)
        {
            this.primitive = primitive;
            callback = fields
                ? new FieldCall((root, mask) => Invoke(
                    [Marshal.ReadIntPtr(root), Marshal.ReadIntPtr(root + nint.Size),
                        Marshal.ReadIntPtr(root + nint.Size * 2)], mask))
                : new DirectCall((first, second, third, mask) =>
                    Invoke([first, second, third], mask));
        }

        private int Invoke(nint[] pointers, int mask)
        {
            Calls++;
            for (int index = 0; index < pointers.Length; index++)
            {
                bool expectedNull = (mask & (1 << index)) != 0;
                if ((pointers[index] == 0) != expectedNull)
                {
                    Matched = false;
                    return -1;
                }
                if (expectedNull)
                {
                    continue;
                }

                byte[] expected = Encode(index + 1);
                byte[] received = new byte[expected.Length];
                Marshal.Copy(pointers[index], received, 0, received.Length);
                Matched &= received.SequenceEqual(expected);
                byte[] updated = Encode(index + 101);
                Marshal.Copy(updated, 0, pointers[index], updated.Length);
            }
            return 42;
        }

        private byte[] Encode(int number) => NativeDllScalarCodec.Encode(
            NativeDllScalarInput.Convert(primitive, Value.Numeric(number), 0));

        public nint Load(string path) => 1;

        public nint GetExport(nint library, string export) =>
            Marshal.GetFunctionPointerForDelegate(callback);

        public void Free(nint library) => Frees++;

        public void Dispose() => GC.KeepAlive(callback);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int DirectCall(nint first, nint second, nint third, int mask);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int FieldCall(nint root, int mask);
    }
}
