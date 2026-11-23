using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task FailedEnumerationDoesNotPreventNextComEnumeration()
    {
        List<string> output = [];
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false);
        await new Interpreter(windows: automation).RunAsync("""
            fso = CREATEOLEOBJ("Scripting.FileSystemObject")
            dictionary = CREATEOLEOBJ("Scripting.Dictionary")
            dictionary.Add("key", 42)
            COM_ERR_IGN
            FOR item IN fso
                PRINT "unexpected"
            NEXT
            PRINT COM_ERR_FLG
            FOR item IN dictionary
                PRINT item
            NEXT
            PRINT COM_ERR_FLG
            COM_ERR_RET
            TRY
                FOR item IN fso
                    PRINT "unexpected"
                NEXT
                PRINT "unexpected"
            EXCEPT
                PRINT COM_ERR_FLG
            ENDTRY
            PRINT dictionary.Item("key")
            """, output.Add);
        Assert.Equal(["1", "key", "1", "1", "42"], output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task DictionaryErrorsStayOnTheCallingScriptThread()
    {
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false);
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC done = FALSE
            parent = CREATEOLEOBJ("Scripting.Dictionary")
            parent.Add("key", 7)
            COM_ERR_IGN
            parent.Add("key", 8)
            THREAD Worker()
            WHILE !done
                SLEEP(0.001)
            WEND
            PRINT COM_ERR_FLG
            PRINT parent.Add("key", 9)
            PRINT parent.Item("key")
            PROCEDURE Worker()
                child = CREATEOLEOBJ("Scripting.Dictionary")
                child.Add("key", 42)
                PRINT COM_ERR_FLG
                TRY
                    child.Add("key", 43)
                    PRINT "unexpected"
                EXCEPT
                    PRINT COM_ERR_FLG
                ENDTRY
                COM_ERR_IGN
                PRINT COM_ERR_FLG
                PRINT child.Item("key")
                COM_ERR_RET
                done = TRUE
            FEND
            """, output.Enqueue, timeout.Token);
        string[] actual = [.. output];
        Assert.Equal(["0", "1", "0", "42", "1", "-999999", "7"], actual);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task DictionaryDuplicateKeyKeepsTypedVarArguments()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            swappedMouseButtons: false)).RunAsync("""
            dictionary = CREATEOLEOBJ("Scripting.Dictionary")
            key = VARTYPE(7, VAR_INTEGER)
            item = VARTYPE(42, VAR_SMALLINT)
            dictionary.Add(VAR key, VAR item)
            PRINT VARTYPE(key)
            PRINT VARTYPE(item)
            PRINT VARTYPE(dictionary.Item(key))
            item = VARTYPE(99, VAR_BYTE)
            COM_ERR_IGN
            result = dictionary.Add(VAR key, VAR item)
            PRINT COM_ERR_FLG
            COM_ERR_RET
            PRINT VARTYPE(result)
            PRINT VARTYPE(key)
            PRINT VARTYPE(item)
            PRINT key + 0
            PRINT item + 0
            PRINT dictionary.Item(key)
            PRINT dictionary.Count
            """, output.Add);
        Assert.Equal(["3", "2", "2", "1", "3", "3", "17", "7", "99",
            "42", "1"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task ComErrorControlKeepsTypedOutputsConsistent(bool fail)
    {
        using var fixture = new DispatchFixture
        {
            HResult = fail ? unchecked((int)0x80020009) : 0,
            DetailedOperation = (arguments, count, result, _, _, _) =>
            {
                Assert.Equal(2u, count);
                int stride = IntPtr.Size == 8 ? 24 : 16;
                Assert.Equal(0x400c, Marshal.ReadInt16(arguments));
                Assert.Equal(0x400c, Marshal.ReadInt16(arguments + stride));
                DispatchFixture.Replace(Marshal.ReadIntPtr(arguments, 8), 42);
                DispatchFixture.Replace(
                    Marshal.ReadIntPtr(arguments + stride, 8), true);
                DispatchFixture.Replace(result, (short)7);
            }
        };
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false, comRuntime: new Runtime(fixture));
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            fixture = CREATEOLEOBJ("fixture")
            first = FALSE
            second = VARTYPE(9, VAR_SMALLINT)
            COM_ERR_IGN
            value = fixture.Change(VAR first, VAR second)
            PRINT VARTYPE(first)
            PRINT VARTYPE(second)
            PRINT VARTYPE(value)
            PRINT first + 0
            PRINT second + 0
            PRINT value + 0
            PRINT COM_ERR_FLG
            COM_ERR_RET
            """, output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal(fail
            ? ["11", "2", "3", "0", "9", "-999999", "1"]
            : new[] { "11", "3", "2", "1", "42", "7", "0" }, output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task SuppressedGetOleItemReturnsMinusOneAndKeepsAdoptedSnapshot()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { }))
            .RunAsync("""
                DIM dictionary=CREATEOLEOBJ("Scripting.Dictionary")
                dictionary.Add("old",7)
                PRINT GETOLEITEM(dictionary)
                DIM target=CREATEOLEOBJ("Scripting.FileSystemObject")
                COM_ERR_IGN
                DIM result=GETOLEITEM(target)
                PRINT result
                PRINT VARTYPE(result)
                PRINT COM_ERR_FLG
                PRINT LENGTH(ALL_OLE_ITEM)
                PRINT ALL_OLE_ITEM[0]
                COM_ERR_RET
                """, output.Add);
        Assert.Equal(["1", "-1", "3", "1", "1", "old"], output);
    }

    [Theory]
    [InlineData("PRINT o.Change()", 1, "-999999")]
    [InlineData("o.Change=1", 1, null)]
    [InlineData("PRINT o.Change(VAR a)", 1, "-999999")]
    [InlineData("PRINT GETOLEITEM(o)", -4, "-1")]
    [InlineData("PRINT LENGTH(o)", -4, "-999999")]
    [InlineData("FOR item IN o\nPRINT 99\nNEXT", -4, null)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task KnownNativeComFailureCanBeSuppressed(
        string operation, int memberId, string? result)
    {
        using var fixture = new DispatchFixture
        {
            MemberId = memberId,
            HResult = unchecked((int)0x80020009),
            DetailedOperation = (_, _, _, _, _, _) => { }
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync(
                "DIM o=CREATEOLEOBJ(\"fixture\")\nDIM a=7\nCOM_ERR_IGN\n"
                + operation + "\nPRINT COM_ERR_FLG\nPRINT a\nCOM_ERR_RET",
                output.Add);
        Assert.Equal(result is null ? ["1", "7"]
            : new[] { result, "1", "7" }, output);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task SuppressedComValueKeepsIntegerVariantType()
    {
        using var fixture = new DispatchFixture
        {
            HResult = unchecked((int)0x80020009),
            DetailedOperation = (_, _, _, _, _, _) => { }
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                COM_ERR_IGN
                DIM value=o.Change()
                PRINT VARTYPE(value)
                PRINT value+10
                PRINT VARTYPE(COM_ERR_FLG)
                """, output.Add);
        Assert.Equal(["3", "-999989", "11"], output);
        Assert.Null(fixture.Failure);
    }
}
