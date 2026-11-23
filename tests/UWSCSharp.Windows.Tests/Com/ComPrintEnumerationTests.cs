using System.Globalization;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Theory]
    [InlineData("direct", false, null)]
    [InlineData("direct", false, "")]
    [InlineData("direct", false, "a\0日本語😀")]
    [InlineData("direct", true, null)]
    [InlineData("direct", true, "")]
    [InlineData("direct", true, "a\0日本語😀")]
    [InlineData("byref", false, null)]
    [InlineData("byref", false, "")]
    [InlineData("byref", false, "a\0日本語😀")]
    [InlineData("byref", true, null)]
    [InlineData("byref", true, "")]
    [InlineData("byref", true, "a\0日本語😀")]
    [InlineData("variant-byref", false, null)]
    [InlineData("variant-byref", false, "")]
    [InlineData("variant-byref", false, "a\0日本語😀")]
    [InlineData("variant-byref", true, null)]
    [InlineData("variant-byref", true, "")]
    [InlineData("variant-byref", true, "a\0日本語😀")]
    public async Task NativeEnumerationBstrPrintAndReplayPreserveString(
        string route, bool useFor, string? text)
    {
        nint bstr = text is null ? 0 : Marshal.StringToBSTR(text);
        nint slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        nint inner = Marshal.AllocCoTaskMem(IntPtr.Size == 8 ? 24 : 16);
        try
        {
            Marshal.WriteIntPtr(slot, bstr);
            Marshal.Copy(new byte[IntPtr.Size == 8 ? 24 : 16], 0, inner,
                IntPtr.Size == 8 ? 24 : 16);
            Marshal.WriteInt16(inner, 8);
            Marshal.WriteIntPtr(inner, 8, bstr);
            using var sequence = new EnumFixture();
            using var fixture = new DispatchFixture { MemberId = -4 };
            int next = 0;
            int replay = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                if (count == 0)
                {
                    sequence.Return(result);
                    return;
                }

                Assert.Equal(1u, count);
                Assert.Equal(8, Marshal.ReadInt16(arguments));
                nint pointer = Marshal.ReadIntPtr(arguments, 8);
                Assert.Equal(replay++ == 0 && text is null, pointer == 0);
                Assert.Equal(text ?? "", pointer == 0
                    ? "" : Marshal.PtrToStringBSTR(pointer));
            };
            sequence.Operation = cell =>
            {
                if (next++ > 0)
                {
                    fixture.MemberId = 1;
                    return (1, 0u);
                }

                if (route == "direct")
                {
                    ComVariantValue.Write(new ComVariantValue(8, text), cell);
                }
                else
                {
                    // BYREFの参照先はこのテストが解放し、列挙側へ所有権を渡しません。
                    Marshal.WriteInt16(cell, route == "byref"
                        ? (short)0x4008 : (short)0x400c);
                    Marshal.WriteIntPtr(cell, 8, route == "byref" ? slot : inner);
                }

                return (0, 1u);
            };
            string body = "PRINT value\nPRINT LENGTH(value)\n"
                + "DIM copiedValue=value\no.Change(copiedValue)\no.Change(value+\"\")\n";
            string receive = useFor ? "FOR value IN o\n" + body + "NEXT"
                : "PRINT GETOLEITEM(o)\nDIM value=ALL_OLE_ITEM[0]\n" + body;
            var output = new List<string>();
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync(
                    "DIM o=CREATEOLEOBJ(\"fixture\")\n" + receive, output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(sequence.Failure);
            Assert.Null(failure);
            string length = (text?.EnumerateRunes().Count() ?? 0).ToString(
                CultureInfo.InvariantCulture);
            Assert.Equal(useFor ? [text ?? "", length]
                : new[] { "1", text ?? "", length }, output);
            Assert.Equal(2, replay);
            Assert.Equal(2, sequence.Calls);
        }
        finally
        {
            Marshal.FreeCoTaskMem(inner);
            Marshal.FreeCoTaskMem(slot);
            Marshal.FreeBSTR(bstr);
        }
    }
}
