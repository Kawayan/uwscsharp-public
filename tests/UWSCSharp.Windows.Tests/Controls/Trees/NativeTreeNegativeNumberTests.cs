using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeNegativeNumberTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    [InlineData(-1, true)]
    public async Task NegativeIncludesDisabledAndUsesAbsoluteMatchNumber(int? number, bool hidden)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint disabled = fixture.Child("SysTreeView32", "", 0x08000000, 10);
        nint disabledFirst = TreeControlTests.Add(disabled, 0, "target");
        nint disabledSecond = TreeControlTests.Add(disabled, 0, "target");
        nint enabled = fixture.Child("SysTreeView32", "", 0, 50);
        nint enabledFirst = TreeControlTests.Add(enabled, 0, "target");
        nint enabledSecond = TreeControlTests.Add(enabled, 0, "target");
        using ToolbarClickTextTests.Interceptor disabledHook = Flags(disabled);
        using ToolbarClickTextTests.Interceptor enabledHook = Flags(enabled);
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        List<Value> arguments = [id, Value.String("target"), Value.Numeric(16), Value.Numeric(1)];
        if (number.HasValue)
        {
            arguments.Add(Value.Numeric(number.Value));
        }

        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", arguments, default));
        Assert.Equal(number < 0 ? number == -2 ? disabledSecond : disabledFirst : 0,
            Native.SendMessageW(disabled, 0x110a, 9, 0));
        Assert.Equal(number < 0 ? 0 : number == 2 ? enabledSecond : enabledFirst,
            Native.SendMessageW(enabled, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task MinimumIntegerFailsBeforeChangingSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, 0, "target");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () => await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(16), Value.Numeric(1), Value.Numeric(int.MinValue)], default));
        Assert.Equal(0, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    private static ToolbarClickTextTests.Interceptor Flags(nint tree) =>
        new(tree, (_, _, _) => null, after: (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                // 番号と候補抽出に検証を絞るため展開済みflagへ固定します。
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
            }
        });
}
