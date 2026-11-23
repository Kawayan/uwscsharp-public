using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class ButtonPairDeliveryDiagnostics(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxOnly")]
    public async Task ObserveRadioStateWithAnInterveningPointerMessage(bool moveBetween)
    {
        nint radio = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            radio = host.Child("BUTTON", "radio", 9, 10, height: 20, width: 100);
        });
        Native.Point point;
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect rect));
            point = new Native.Point(rect.Right - 5, rect.Bottom - 5);
        }

        await UiInputDriver.ActivateAsync(fixture.Root, point);
        fixture.Invoke(() =>
        {
            Native.SendMessageW(radio, 0x201, 0, 0);
            nint pressed = Native.SendMessageW(radio, 0xf2, 0, 0);
            if (moveBetween)
            {
                Native.SendMessageW(radio, 0x200, 0, 0x1900190);
            }

            nint beforeUp = Native.SendMessageW(radio, 0xf2, 0, 0);
            Native.SendMessageW(radio, 0x202, 0, 0);
            nint check = Native.SendMessageW(radio, 0xf0, 0, 0);
            var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
            Assert.True(Native.GetGUIThreadInfo(Native.GetCurrentThreadId(), ref info));
            output.WriteLine($"intervening_move={moveBetween} pressed={pressed} before_up={beforeUp} check={check} active={info.Active:X} foreground={Native.GetForegroundWindow():X} capture={info.Capture:X}");
            if (!moveBetween)
            {
                Assert.Equal((nint)1, check);
            }
        });
    }
}
