using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class MessageBoxAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task All127ButtonCombinationsFitAndHaveOnlyTheirRequestedButtons()
    {
        await using var service = new NativeDialogService();
        int[] order = [4, 8, 1, 2, 16, 32, 64];
        for (int mask = 1; mask <= 127; mask++)
        {
            using var timer = new TestPhaseTimer(output, $"message-box:{mask}");
            string title = $"uwsc-mask-{mask}-" + Guid.NewGuid();
            Task<int> show = service.ShowMessageAsync(new object(),
                new("確認", mask, null, null, 0, false), new(title), 0, default);
            nint window;
            using (var discovery = new TestPhaseTimer(output, $"message-box-discovery:{mask}"))
            {
                window = await MessageBoxIntegrationTests.FindWindowAsync(title);
            }
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.GetWindowRect(window, out Native.Rect parent));
                foreach (int bit in order)
                {
                    nint button = MessageBoxIntegrationTests.GetDlgItem(window, 4096 + bit);
                    Assert.Equal((mask & bit) != 0, button != 0);
                    if (button == 0)
                    {
                        continue;
                    }

                    Assert.True(Native.IsWindowVisible(button));
                    Assert.True(Native.GetWindowRect(button, out Native.Rect rect));
                    Assert.True(rect.Left >= parent.Left && rect.Top >= parent.Top);
                    Assert.True(rect.Right <= parent.Right && rect.Bottom <= parent.Bottom);
                }
            }

            int expected = order.First(bit => (mask & bit) != 0);
            nint first = MessageBoxIntegrationTests.GetDlgItem(window, 4096 + expected);
            Native.PostMessageW(first, 0x100, 13, 0);
            Native.PostMessageW(first, 0x101, 13, 0);
            Assert.Equal(expected, await show.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("http://example.invalid/")]
    [InlineData("mailto:test@example.invalid")]
    public async Task NativeMouseClickReachesTheDetectedLinkWithoutLaunchingExternalApps(string address)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new NativeDialogService(uri => opened.TrySetResult(uri));
        string title = "uwsc-mouse-link-" + Guid.NewGuid();
        Task<int> show = service.ShowMessageAsync(new object(),
            new(address, 1, null, null, 0, true), new(title), 0, default);
        nint window = await MessageBoxIntegrationTests.FindWindowAsync(title);
        nint text = MessageBoxIntegrationTests.GetDlgItem(window, 200);
        await UiInputDriver.ActivateAsync(window);
        await Task.Delay(150);
        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        nint buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            Assert.NotEqual(0, Native.SendMessageTimeoutW(text, 0x426, buffer, 4,
                2, 1000, out _));
            var point = new Native.Point
            {
                X = Marshal.ReadInt32(buffer) + 2,
                Y = Marshal.ReadInt32(buffer, 4) + 5
            };
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.ClientToScreen(text, ref point));
                Assert.True(Native.SetCursorPos(point.X, point.Y));
            }

            await Task.Delay(100);
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

            Assert.Equal(address, (await opened.Task.WaitAsync(TimeSpan.FromSeconds(5))).AbsoluteUri);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            using (var dpi = new PhysicalDpiScope())
            {
                Native.SetCursorPos(previous.X, previous.Y);
            }

            Native.PostMessageW(window, 0x10, 0, 0);
        }

        await show.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
