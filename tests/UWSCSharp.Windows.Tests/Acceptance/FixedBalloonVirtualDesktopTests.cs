using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class FixedBalloonVirtualDesktopTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task DesktopChildRemainsRenderedWhenTheOwnedControlWindowIsCloaked()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        await using IWindowsAutomationSession control = new WindowsAutomation().CreateSession();
        await using IWindowsAutomationSession fixedSession = new WindowsAutomation().CreateSession();
        fixedSession.ConfigureDialogOption(Keywords.FixBalloon, [], default);
        nint controlWindow = await Show(control, 600);
        nint fixedWindow = await Show(fixedSession, 80);
        Assert.Equal(0, Cloaked(controlWindow));
        Assert.Equal(0, Cloaked(fixture.Root));
        int before = await WaitForColor(fixedWindow);
        output.WriteLine($"before-color-pixels={before}");
        Assert.True(before > 100, "デスクトップ直下の吹出しの描画条件が不成立です。");
        bool changedDesktop = false;
        try
        {
            Shortcut(0x44);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                // 所有者のないツールウィンドウを、切替成立の対照には使いません。
                if (Cloaked(fixture.Root) != 0)
                {
                    changedDesktop = true;
                    break;
                }

                await Task.Delay(25);
            }

            output.WriteLine($"control-cloaked={Cloaked(fixture.Root)},"
                + $"balloon-cloaked={Cloaked(controlWindow)},changed-desktop={changedDesktop}");
            Assert.True(changedDesktop, "仮想デスクトップ作成・切替の対照条件が不成立です。");
            int after = await WaitForColor(fixedWindow);
            output.WriteLine($"after-color-pixels={after}");
            Assert.True(Native.IsWindowVisible(fixedWindow));
            Assert.True(after > 100, "切替後に固定吹出しの背景色を確認できません。");
        }
        finally
        {
            if (changedDesktop)
            {
                Shortcut(0x73);
                var timer = Stopwatch.StartNew();
                while (Cloaked(fixture.Root) != 0
                    && timer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(25);
                }

                output.WriteLine($"restored-control-cloaked={Cloaked(fixture.Root)}");
            }
        }

        Assert.Equal(0, Cloaked(fixture.Root));
    }

    private static async Task<nint> Show(IWindowsAutomationSession session, int x)
    {
        await session.InvokeAsync(BuiltinFunctionNames.Balloon,
            [Value.String("desktop probe"), Value.Numeric(x), Value.Numeric(90),
                Value.Numeric(0), Value.Numeric(16), Value.String("Arial"),
                Value.Numeric(0), Value.Numeric(0x13579B)], default);
        Value id = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(BuiltinWindowSelectors.GetFukidasiWin)], default);
        Value handle = await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [id], default);
        return checked((nint)(long)handle.AsNumber());
    }

    private static async Task<int> WaitForColor(nint window)
    {
        var timer = Stopwatch.StartNew();
        int count;
        do
        {
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.GetWindowRect(window, out Native.Rect bounds));
                PixelImage image = new NativeImageCapture().Capture(new(bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), default);
                count = image.Pixels.Count(pixel => (pixel & 0xffffff) == 0x9B5713);
            }

            if (count > 100)
            {
                return count;
            }

            await Task.Delay(25);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        return count;
    }

    private static int Cloaked(nint window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)));
        return cloaked;
    }

    private static void Shortcut(ushort key)
    {
        try
        {
            Send(0x5B, false);
            Send(0x11, false);
            Send(key, false);
        }
        finally
        {
            Send(key, true);
            Send(0x11, true);
            Send(0x5B, true);
        }

        static void Send(ushort key, bool up) => NativeInputSender.Send(new Native.Input
        {
            Type = 1,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.KeyboardInput { Vk = key, Flags = up ? 2u : 0u }
            }
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute,
        out int value, int size);
}
