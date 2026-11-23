using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MultipleSelectionMessageContractTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(0x800)]
    [InlineData(0x801)]
    public void NativeSettersKeepPartialSelectionWithoutParentNotifications(int style)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", style, 10);
        foreach (string text in new[] { "dup", "dup", "last" })
        {
            Native.SendText(control, 0x180, 0, text, 2, 1000, out _);
        }

        int notifications = 0;
        fixture.Command = (_, _) => notifications++;
        Native.SendMessageW(control, 0x185, 1, 2);
        Native.SendMessageW(control, 0x185, 0, -1);
        Native.SendMessageW(control, 0x185, 1, 0);
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x187, 0, 0));
        Assert.Equal((nint)0, Native.SendMessageW(control, 0x187, 2, 0));
        // 一語指定時に原本が送るSETCURSELは、複数選択LISTでは成功しません。
        Assert.Equal((nint)(-1), Native.SendMessageW(control, 0x186, 0, 0));
        Native.SendMessageW(control, 0x185, 1, 1);
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x187, 0, 0));
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x187, 1, 0));
        Assert.Equal(0, notifications);
        Assert.Null(fixture.CallbackFailure);
    }
}
