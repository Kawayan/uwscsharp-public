using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task NativeReturnAndReferenceOutputsShareReadBudget(int extra)
    {
        using var fixture = new DispatchFixture();
        int half = UwscArray.MaximumElements / 2;
        fixture.DetailedOperation = (arguments, count, result, _, _, _) =>
        {
            nint output = DispatchFixture.Argument(arguments, 0, count);
            DispatchFixture.Replace(Marshal.ReadIntPtr(output, 8), new int[half]);
            DispatchFixture.Replace(result, new int[half + extra]);
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
            o=CREATEOLEOBJ("fixture")
            a=7
            TRY
                result=o.Change(VAR a)
                PRINT LENGTH(a)
                PRINT LENGTH(result)
            EXCEPT
                PRINT TRY_ERRMSG
                PRINT a
            ENDTRY
            """, output.Add);
        Assert.Null(fixture.Failure);
        if (extra == 0)
        {
            Assert.Equal(["500000", "500000"], output);
        }
        else
        {
            Assert.Equal(2, output.Count);
            Assert.Contains("COM値の累積要素数が上限を超えました", output[0]);
            Assert.Equal("7", output[1]);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task NativeOutputOverBudgetReleasesUnpublishedReference()
    {
        using var fixture = new DispatchFixture();
        using var child = new DispatchFixture();
        int half = UwscArray.MaximumElements / 2;
        fixture.Operation = (arguments, count, result) =>
        {
            nint reference = Marshal.ReadIntPtr(
                DispatchFixture.Argument(arguments, 0, count), 8);
            DispatchFixture.Replace(reference, null);
            Marshal.AddRef(child.Pointer);
            Marshal.WriteIntPtr(reference, 8, child.Pointer);
            Marshal.WriteInt16(reference, 9);
            nint array = Marshal.ReadIntPtr(
                DispatchFixture.Argument(arguments, 1, count), 8);
            DispatchFixture.Replace(array, new int[half]);
            DispatchFixture.Replace(result, new int[half + 1]);
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
            o=CREATEOLEOBJ("fixture")
            a=7
            b=8
            TRY
                result=o.Change(VAR a, VAR b)
            EXCEPT
                PRINT TRY_ERRMSG
                PRINT a
                PRINT b
            ENDTRY
            """, output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal(3, output.Count);
        Assert.Contains("COM値の累積要素数が上限を超えました", output[0]);
        Assert.Equal(["7", "8"], output.Skip(1));
        // child.Dispose で参照数が初期値へ戻ったことを確認します。
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("number")]
    [InlineData("out")]
    [InlineData("next")]
    [InlineData("date")]
    [InlineData("array-null")]
    [InlineData("enum-value")]
    [InlineData("enum-interface")]
    public async Task ComValueValidationReachesExcept(string condition)
    {
        using var sequence = new EnumFixture { Operation = _ => (0, 2) };
        using var fixture = new DispatchFixture();
        string call;
        string setup = "";
        if (condition == "hash")
        {
            setup = "HASHTBL h\n";
            call = "o.Change(h)";
        }
        else if (condition == "number")
        {
            call = "PRINT o.Change()";
            fixture.Operation = (_, _, result) =>
                ComVariantValue.Write(double.NaN, result);
        }
        else if (condition == "out")
        {
            call = "o.Change(VAR a)";
            fixture.Operation = (arguments, count, _) =>
                ComVariantValue.Write(double.NaN,
                    Marshal.ReadIntPtr(DispatchFixture.Argument(arguments, 0, count), 8));
        }
        else if (condition is "date" or "array-null")
        {
            call = "PRINT o.Change()";
            fixture.Operation = (_, _, result) =>
            {
                Marshal.WriteInt16(result, condition == "date" ? (short)7 : (short)0x200c);
                Marshal.WriteInt64(result, 8,
                    condition == "date" ? BitConverter.DoubleToInt64Bits(double.NaN) : 0);
            };
        }
        else if (condition is "enum-value" or "enum-interface")
        {
            call = "PRINT GETOLEITEM(o)";
            fixture.MemberId = -4;
            fixture.Operation = (_, _, result) =>
            {
                if (condition == "enum-value")
                {
                    ComVariantValue.Write(42d, result);
                }
                else
                {
                    Marshal.AddRef(fixture.Pointer);
                    Marshal.WriteIntPtr(result, 8, fixture.Pointer);
                    Marshal.WriteInt16(result, 9);
                }
            };
        }
        else
        {
            call = "PRINT GETOLEITEM(o)";
            fixture.MemberId = -4;
            fixture.Operation = (_, _, result) => sequence.Return(result);
        }

        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\na=7\n" + setup
                + "TRY\n" + call
                + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT a",
                output.Add);
        Assert.Equal(["caught", "7"], output);
        Assert.Null(fixture.Failure);
        Assert.Null(sequence.Failure);
        if (condition == "hash")
        {
            Assert.Equal(0, fixture.InvokeThread);
        }
    }

    private sealed class UnclassifiedRuntime(Exception failure) : IComRuntime
    {
        public object Create(string name) => new object();

        public object GetActive(string name) => throw failure;

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign) => throw failure;

        public void Release(object target)
        {
        }
    }

    [Fact]
    public async Task InvalidEnumerationKeepsPublishedOleItems()
    {
        using var sequence = new EnumFixture();
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        int step = 0;
        sequence.Operation = cell =>
        {
            switch (step++)
            {
                case 0:
                    ComVariantValue.Write(7d, cell);
                    return (0, 1u);
                case 2:
                    ComVariantValue.Write(8d, cell);
                    return (0, 1u);
                case 3:
                    return (0, 2u);
                case 4:
                    ComVariantValue.Write(9d, cell);
                    return (0, 1u);
                default:
                    return (1, 0u);
            }
        };
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nPRINT GETOLEITEM(o)\n"
                + "TRY\nPRINT GETOLEITEM(o)\nEXCEPT\nPRINT ALL_OLE_ITEM[0]\n"
                + "ENDTRY\nPRINT LENGTH(ALL_OLE_ITEM)\n"
                + "PRINT GETOLEITEM(o)\nPRINT ALL_OLE_ITEM[0]", output.Add);
        Assert.Equal(["1", "7", "1", "1", "9"], output);
        Assert.Null(fixture.Failure);
        Assert.Null(sequence.Failure);
    }

    [Theory]
    [InlineData("o.Change()", 1)]
    [InlineData("o.Change = 1", 1)]
    [InlineData("o.Change(VAR a)", 1)]
    [InlineData("PRINT GETOLEITEM(o)", -4)]
    [InlineData("PRINT LENGTH(o)", -4)]
    [InlineData("FOR x IN o\nPRINT x\nNEXT", -4)]
    public async Task NativeComFailureReachesScriptExcept(
        string call, int memberId)
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
                "o=CREATEOLEOBJ(\"fixture\")\na=7\nTRY\n" + call
                + "\nEXCEPT\nPRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\n"
                + "ENDTRY\nPRINT a\nPRINT \"after\"", output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal(4, output.Count);
        Assert.Equal("4", output[0]);
        Assert.Contains("COM呼出しに失敗", output[1]);
        Assert.Equal("7", output[2]);
        Assert.Equal("after", output[3]);
    }

    [Theory]
    [InlineData("CREATEOLEOBJ")]
    [InlineData("GETACTIVEOLEOBJ")]
    public async Task MissingComClassCanBeHandledAndCreationContinues(
        string function)
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { })).RunAsync(
            "TRY\nPRINT " + function + "(\"UWSCSharp.Missing."
            + Guid.NewGuid().ToString("N")
            + "\")\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\n"
            + "o=CREATEOLEOBJ(\"Scripting.Dictionary\")\nPRINT o.Count",
            output.Add);
        Assert.Equal(["2", "0"], output);
    }

    [Fact]
    public async Task UnknownMemberNameIsCatchable()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { })).RunAsync(
            "o=CREATEOLEOBJ(\"Scripting.Dictionary\")\nTRY\n"
            + "o.UwscMissingMember()\nEXCEPT\nPRINT TRY_ERRLINE\n"
            + "ENDTRY\nPRINT o.Count", output.Add);
        Assert.Equal(["3", "0"], output);
    }

    [Theory]
    [InlineData("PRINT GETOLEITEM(o)", unchecked((int)0x80004005))]
    [InlineData("PRINT LENGTH(o)", unchecked((int)0x80070005))]
    [InlineData("FOR x IN o\nPRINT x\nNEXT", unchecked((int)0x80004001))]
    public async Task NativeNextFailureIsCatchable(string call, int hresult)
    {
        using var sequence = new EnumFixture
        {
            Operation = _ => (hresult, 0)
        };
        using var fixture = new DispatchFixture { MemberId = -4 };
        fixture.Operation = (_, _, result) => sequence.Return(result);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nTRY\n" + call
                + "\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY\nPRINT \"after\"",
                output.Add);
        Assert.Equal(["3", "after"], output);
        Assert.Equal(1, sequence.Calls);
        Assert.Null(sequence.Failure);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    public async Task UncaughtNativeFailureKeepsComMetadataAndFinally()
    {
        using var fixture = new DispatchFixture
        {
            HResult = unchecked((int)0x80020009),
            Operation = (_, _, _) => { },
            ExceptionInfo = () => new EXCEPINFO
            {
                bstrDescription = "対象の操作失敗",
                bstrSource = "UWSC test source",
                scode = unchecked((int)0x80004005)
            }
        };
        List<string> output = [];
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nTRY\no.Change()\n"
                    + "FINALLY\nPRINT \"finally\"\nENDTRY\nPRINT \"after\"",
                    output.Add));
        Assert.Equal(3, failure.Line);
        Assert.Contains("対象の操作失敗", failure.Message);
        COMException cause = Assert.IsType<COMException>(
            failure.InnerException?.InnerException);
        Assert.Equal(unchecked((int)0x80004005), cause.HResult);
        Assert.Equal("UWSC test source", cause.Source);
        Assert.Equal(["finally"], output);
        Assert.Null(fixture.Failure);
    }

    [Theory]
    [InlineData(unchecked((int)0x8007000e), 0)]
    [InlineData(unchecked((int)0x8007000e), unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80020009), unchecked((int)0x8007000e))]
    public async Task NativeOutOfMemoryHresultRemainsHostFailure(
        int invokeResult, int exceptionCode)
    {
        using var fixture = new DispatchFixture
        {
            HResult = invokeResult,
            Operation = (_, _, _) => { },
            ExceptionInfo = () => new EXCEPINFO { scode = exceptionCode }
        };
        List<string> output = [];
        COMException failure = await Assert.ThrowsAsync<COMException>(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nTRY\no.Change()\n"
                    + "EXCEPT\nPRINT \"caught\"\nENDTRY", output.Add));
        Assert.Equal(exceptionCode == 0 ? invokeResult : exceptionCode,
            failure.HResult);
        Assert.Empty(output);
        Assert.Null(fixture.Failure);
    }

    [Fact]
    public async Task UnknownRuntimeComExceptionRemainsHostFailure()
    {
        var failure = new COMException("未分類runtime障害");
        List<string> output = [];
        COMException actual = await Assert.ThrowsAsync<COMException>(() =>
            new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new UnclassifiedRuntime(failure))).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nTRY\no.Change()\n"
                    + "EXCEPT\nPRINT \"caught\"\nENDTRY", output.Add));
        Assert.Same(failure, actual);
        Assert.Empty(output);
    }

    [Fact]
    public async Task DirectComApiKeepsTypeAndSessionOwnership()
    {
        await using var session = new ComSession();
        await using var other = new ComSession();
        Value target = await session.CreateAsync(
            "Scripting.Dictionary", false, default);
        COMException failure = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync(target, "UwscMissingMember", [], null, default));
        Assert.Equal(failure.Message, session.GetScriptErrorMessage(failure));
        Assert.Null(other.GetScriptErrorMessage(failure));
        Assert.Null(session.GetScriptErrorMessage(
            new COMException(failure.Message, failure.HResult)));
    }
}
