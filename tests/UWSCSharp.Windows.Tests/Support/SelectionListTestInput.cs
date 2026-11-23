using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

internal static class SelectionListTestInput
{
    internal static async Task SelectAsync(
        nint window, nint list, string action, ITestOutputHelper output)
    {
        _ = Native.SetForegroundWindow(window);
        Assert.Equal(window, Native.GetForegroundWindow());
        ClickListItem(list, 0);
        await WaitForListStateAsync(list, 0x187, 0, 1);
        Assert.Equal(0, Native.SendMessageW(list, 0x187, 2, 0));

        SendKey(0x11, 0);
        try
        {
            Assert.Equal(window, Native.GetForegroundWindow());
            if (action == "control-click")
            {
                ClickListItem(list, 2);
            }
            else
            {
                SendKey(0x23, 0);
                SendKey(0x23, 2);
                await WaitForListStateAsync(list, 0x19f, 0, 2);
                output.WriteLine($"first_after_end={Native.SendMessageW(list, 0x187, 0, 0)}");
                Assert.Equal(window, Native.GetForegroundWindow());
                SendKey(0x20, 0);
                SendKey(0x20, 2);
            }

            await WaitForListStateAsync(list, 0x187, 2, 1);
            output.WriteLine($"selection={Native.SendMessageW(list, 0x187, 0, 0)},"
                + $"{Native.SendMessageW(list, 0x187, 1, 0)},"
                + $"{Native.SendMessageW(list, 0x187, 2, 0)}");
        }
        finally
        {
            SendKey(0x11, 2);
        }
    }

    private static async Task WaitForListStateAsync(
        nint list, uint message, int argument, int expected)
    {
        var timer = Stopwatch.StartNew();
        while (Native.SendMessageW(list, message, argument, 0) != expected
            && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.Equal((nint)expected,
            Native.SendMessageW(list, message, argument, 0));
    }

    private static unsafe void ClickListItem(nint list, int index)
    {
        using var dpi = new PhysicalDpiScope();
        Native.Rect bounds = default;
        Assert.NotEqual(-1,
            Native.SendMessageW(list, 0x198, index, (nint)(&bounds)));
        var point = new Native.Point((bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2);
        Assert.True(Native.ClientToScreen(list, ref point));
        Assert.Equal(list, Native.WindowFromPoint(point));
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point previous));
        try
        {
            Assert.True(Native.SetCursorPos(point.X, point.Y));
            foreach (uint flags in new uint[] { 2, 4 })
            {
                NativeInputSender.Send(new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputUnion
                    {
                        Mouse = new Native.MouseInput { Flags = flags }
                    }
                });
            }
        }
        finally
        {
            Native.SetCursorPos(previous.X, previous.Y);
        }
    }

    private static void SendKey(ushort key, uint flags) =>
        NativeInputSender.Send(new Native.Input
        {
            Type = 1,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags }
            }
        });
}
