using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeSendStrTests
{
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    private static nuint Message(nint target, uint message,
        nint wParam = 0, nint lParam = 0)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, message,
            wParam, lParam, Native.SmtoAbortIfHung,
            Native.MessageTimeoutMilliseconds, out nuint result));
        return result;
    }

    [Theory]
    [InlineData(0, 0, "abXYef", true)]
    [InlineData(1, 0, "abcdefXY", false)]
    [InlineData(0, 1, "XY", false)]
    [InlineData(1, 1, "XY", false)]
    public async Task SelectionAppendAndReplacementKeepTheirEditingContracts(
        int occurrence, int mode, string expected, bool undo)
    {
        nint edit = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                edit = owner.Child("EDIT", "abcdef", 0, 20);
                _ = SetFocus(edit);
                _ = Message(edit, 0xb1, 2, 4);
                _ = Message(edit, 0xcd);
                _ = Message(edit, 0xb9);
            });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync(BuiltinFunctionNames.SendStr,
            [id, Value.String("XY"), Value.Numeric(occurrence),
                Value.Numeric(mode)], default);
        Assert.Equal(Value.String(expected), await session.InvokeAsync(
            BuiltinFunctionNames.GetStr, [id, Value.Numeric(1)], default));
        Assert.Equal(1u, Message(edit, 0xb8));
        Assert.Equal(undo ? 1u : 0u, Message(edit, 0xc6));
        _ = Message(edit, 0xc7);
        Assert.Equal(Value.String(undo ? "abcdef" : expected),
            await session.InvokeAsync(BuiltinFunctionNames.GetStr,
                [id, Value.Numeric(1)], default));
    }

    [Theory]
    [InlineData(1, 0, "abcdefXY")]
    [InlineData(1, 1, "XY")]
    [InlineData(2, 1, "XY")]
    public async Task MsaaWritesAnOwnedNativeEdit(int selector,
        int mode, string expected)
    {
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                owner.Child("EDIT", "abcdef", 0, 20), initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync(BuiltinFunctionNames.SendStr,
            [id, Value.String("XY"), Value.Numeric(1), Value.Numeric(mode),
                Value.Numeric(selector)], default);
        Assert.Equal(Value.String(expected), await session.InvokeAsync(
            BuiltinFunctionNames.GetStr, [id, Value.Numeric(1)], default));
    }

    [Fact]
    public async Task EmptyFocusedAppendDoesNotDeleteTheSelection()
    {
        nint edit = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                edit = owner.Child("EDIT", "abcdef", 0, 20);
                _ = SetFocus(edit);
                _ = Message(edit, 0xb1, 2, 4);
                _ = Message(edit, 0xcd);
                _ = Message(edit, 0xb9);
            });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync(BuiltinFunctionNames.SendStr,
            [id, Value.String("")], default);
        Assert.Equal(Value.String("abcdef"), await session.InvokeAsync(
            BuiltinFunctionNames.GetStr, [id, Value.Numeric(1)], default));
        Assert.Equal(0u, Message(edit, 0xb8));
        Assert.Equal(0u, Message(edit, 0xc6));
        Assert.Equal(0x00040002u, Message(edit, 0xb0));
    }
}
