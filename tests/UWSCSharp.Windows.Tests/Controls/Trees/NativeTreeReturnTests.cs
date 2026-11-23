using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeReturnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VetoKeepsCaretButDoesNotRejectMatchedItem(bool query)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint other = TreeControlTests.Add(tree, 0, "other");
        nint target = TreeControlTests.Add(tree, 0, "target");
        ViewCheckTests.SetState(tree, target, false, 2);
        Native.SendMessageW(tree, 0x110b, 9, other);
        fixture.Notify = (_, pointer) =>
            Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code == -450 ? 1 : 0;
        int states = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 8)
            {
                states++;
                Assert.Equal(target, Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4));
            }

            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(),
            listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = query ? [id, Value.String("target")]
            : [id, Value.String("target"), Value.Numeric(16), Value.Numeric(0)];
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            query ? "CHKBTN" : "CLKITEM", args, default));
        Assert.Equal(other, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Equal(0x2000, (int)Native.SendMessageW(tree, 0x1127, target, 0xf000));
        Assert.Equal(1, states);
        Assert.Null(fixture.CallbackFailure);
    }
}
