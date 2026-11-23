using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class ComReferenceMarshallingTests
{
    [Theory]
    [InlineData("direct", null)]
    [InlineData("direct", "")]
    [InlineData("direct", "a\0日本語😀")]
    [InlineData("byref", null)]
    [InlineData("byref", "")]
    [InlineData("byref", "a\0日本語😀")]
    [InlineData("out", null)]
    [InlineData("out", "")]
    [InlineData("out", "a\0日本語😀")]
    [InlineData("array", null)]
    [InlineData("array", "")]
    [InlineData("array", "a\0日本語😀")]
    [InlineData("variant-array", null)]
    [InlineData("variant-array", "")]
    [InlineData("variant-array", "a\0日本語😀")]
    public async Task NativeBstrPrintPreservesStringKindAndEmbeddedNull(
        string route, string? text)
    {
        nint bstr = text is null ? 0 : Marshal.StringToBSTR(text);
        nint slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        bool transferred = false;
        try
        {
            Marshal.WriteIntPtr(slot, bstr);
            using var fixture = new DispatchFixture();
            int call = 0;
            fixture.Operation = (arguments, count, result) =>
            {
                int invocation = call++;
                if (invocation > 0)
                {
                    Assert.Equal(8, Marshal.ReadInt16(arguments));
                    nint receivedPointer = Marshal.ReadIntPtr(arguments, 8);
                    if (route is not ("array" or "variant-array"))
                    {
                        Assert.Equal(invocation == 1 && text is null,
                            receivedPointer == 0);
                    }
                    string? received = receivedPointer == 0
                        ? null : Marshal.PtrToStringBSTR(receivedPointer);
                    Assert.Equal(text ?? "", received ?? "");
                    return;
                }

                if (route is "array" or "variant-array")
                {
                    ComVariantValue.Write(new ComArrayValue(
                        route == "array" ? (ushort)8 : (ushort)12,
                        [0], [0], [new ComVariantValue(8, text)]), result);
                    return;
                }

                nint destination = route == "out"
                    ? Marshal.ReadIntPtr(
                        DispatchFixture.Argument(arguments, 0, count), 8)
                    : result;
                Marshal.WriteIntPtr(destination, 8, route == "byref" ? slot : bstr);
                Marshal.WriteInt16(destination, route == "byref" ? (short)0x4008 : (short)8);
                transferred = route != "byref";
            };
            string receive = route == "out"
                ? "DIM v\no.Change(VAR v)"
                : route is "array" or "variant-array"
                    ? "DIM a=o.Change()\nDIM v=a[0]"
                : "DIM v=o.Change()";
            var output = new List<string>();
            Exception? failure = await Record.ExceptionAsync(() =>
                new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync(
                    "DIM o=CREATEOLEOBJ(\"fixture\")\n" + receive
                    + "\nPRINT v\nPRINT LENGTH(v)"
                    + "\nDIM alias=v\no.Change(alias)\no.Change(v+\"\")", output.Add));
            Assert.Null(fixture.Failure);
            Assert.Null(failure);
            Assert.Equal(3, call);
            Assert.Equal([text ?? "", (text?.EnumerateRunes().Count() ?? 0).ToString(
                CultureInfo.InvariantCulture)], output);
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            if (!transferred)
            {
                Marshal.FreeBSTR(bstr);
            }
        }
    }

    [Fact]
    public async Task NativeNullBstrPublicMutationDoesNotReplayNull()
    {
        using var fixture = new DispatchFixture();
        int call = 0;
        fixture.Operation = (arguments, _, result) =>
        {
            if (call++ == 0)
            {
                Marshal.WriteIntPtr(result, 8, 0);
                Marshal.WriteInt16(result, 8);
                return;
            }

            Assert.Equal(8, Marshal.ReadInt16(arguments));
            nint pointer = Marshal.ReadIntPtr(arguments, 8);
            Assert.NotEqual(0, pointer);
            Assert.Equal("changed", Marshal.PtrToStringBSTR(pointer));
        };
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Value received = await session.InvokeAsync(root, "Change", [], null, default);
        Assert.Equal(ValueKind.String, received.Kind);
        Assert.Equal(Value.String(""), received);
        Assert.Equal(Value.String("").GetHashCode(), received.GetHashCode());
        Value modified = received with
        {
            Text = "changed"
        };
        await session.InvokeAsync(root, "Change", [modified], null, default);
        Assert.Null(fixture.Failure);
        Assert.Equal(2, call);
    }
}
