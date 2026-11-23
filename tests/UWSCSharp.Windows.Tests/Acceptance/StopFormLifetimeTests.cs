using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class StopFormLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOptionsRestoreTheActualWindowStyle(bool cancellation)
    {
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(_ => { }), () => { });
        nint window = await host.GetWindowHandleAsync(CancellationToken.None);
        WindowsStopFormLayout.SetTopMost(window, false);
        nint foreground = Native.GetForegroundWindow();
        using var token = new CancellationTokenSource();
        await using (IScriptStopFormOptionsSession session =
            await host.CreateStopFormOptionsSessionAsync(token.Token))
        {
            await session.ApplyAsync(ScriptStopFormOption.TopMost, token.Token);
            await session.ApplyAsync(ScriptStopFormOption.DisableStopHotkey, token.Token);
            Assert.True(WindowsStopFormLayout.IsTopMost(window));
            Assert.False(Native.IsWindowVisible(window));
            Assert.Equal(foreground, Native.GetForegroundWindow());
            if (cancellation)
            {
                token.Cancel();
            }
        }

        Assert.False(WindowsStopFormLayout.IsTopMost(window));
        Assert.True(Native.IsWindow(window));
        output.WriteLine($"cancel={cancellation} hidden=true foreground_unchanged=true restored=true");
    }

    [Fact]
    public async Task NoStopOptionDoesNotRestoreAnUnchangedTopMostSetting()
    {
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(_ => { }), () => { });
        nint window = await host.GetWindowHandleAsync(CancellationToken.None);
        IScriptStopFormOptionsSession session = await host.CreateStopFormOptionsSessionAsync(default);
        await session.ApplyAsync(ScriptStopFormOption.DisableStopHotkey, default);
        WindowsStopFormLayout.SetTopMost(window, false);
        await session.DisposeAsync();
        Assert.False(WindowsStopFormLayout.IsTopMost(window));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ApplyAsync(ScriptStopFormOption.TopMost, default).AsTask());
    }

    [Fact]
    public async Task NativeStopCaptionHasVisibleGlyphsInTheSelectedFont()
    {
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(_ => { }), () => { });
        nint window = await host.GetWindowHandleAsync(CancellationToken.None);
        nint button = GetDlgItem(window, 1);
        Assert.NotEqual(0, button);
        nint font = SendMessageW(button, 0x31, 0, 0);
        Assert.NotEqual(0, font);
        nint dc = CreateCompatibleDC(0);
        Assert.NotEqual(0, dc);
        nint previous = SelectObject(dc, font);
        try
        {
            var face = new StringBuilder(128);
            Assert.NotEqual(0, GetTextFaceW(dc, face.Capacity, face));
            ushort[] glyphs = new ushort[2];
            Assert.Equal(2u, GetGlyphIndicesW(dc, "停止", 2, glyphs, 1));
            output.WriteLine($"selected_face={face} glyphs={string.Join(',', glyphs)}");
            Assert.DoesNotContain(ushort.MaxValue, glyphs);
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteDC(dc);
        }
    }

    [Fact]
    public async Task WorkerSessionsDoNotOwnTheHostsSharedWindow()
    {
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(_ => { }), () => { });
        nint handle = await host.GetWindowHandleAsync(CancellationToken.None);
        var automation = WindowsAutomation.WithHostWindows((kind, token) =>
            kind == WindowsHostWindow.Stop ? host.GetWindowHandleAsync(token) : ValueTask.FromResult((nint)0));
        await using IWindowsAutomationSession root = automation.CreateSession();
        await using (IWindowsAutomationSession child = root.Fork())
        {
            Assert.Equal(handle, await ReadHandle(child));
        }
        Assert.True(Native.IsWindow(handle));
        Assert.Equal(handle, await ReadHandle(root));
        await root.DisposeAsync();
        await host.DisposeAsync();
        Assert.False(Native.IsWindow(handle));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.GetWindowHandleAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DialogTitleDoesNotChangeTheSharedStopWindow()
    {
        await using var host = new WindowsStopFormHost(
            new CallbackScriptHost(_ => { }), () => { });
        var automation = WindowsAutomation.WithHostWindows((kind, token) =>
            kind == WindowsHostWindow.Stop ? host.GetWindowHandleAsync(token)
                : ValueTask.FromResult((nint)0));
        await using IWindowsAutomationSession root = automation.CreateSession();
        nint window = await ReadHandle(root);
        Assert.NotEqual(0, window);
        string original = MessageBoxIntegrationTests.ReadText(window);
        Assert.NotEmpty(original);

        await using IWindowsAutomationSession child = root.Fork();
        child.ConfigureDialogOption(Keywords.DlgTitle,
            [Value.String("停止画面とは別のタイトル")], default);

        Assert.Equal(window, await ReadHandle(child));
        Assert.Equal(window, await ReadHandle(root));
        Assert.Equal(original, MessageBoxIntegrationTests.ReadText(window));
    }

    private static async Task<nint> ReadHandle(IWindowsAutomationSession session)
    {
        Value id = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(BuiltinWindowSelectors.GetStopFormWin)], CancellationToken.None);
        Value handle = await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [id], CancellationToken.None);
        return (nint)(long)handle.AsNumber();
    }

    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(nint dc, int count, StringBuilder face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(nint dc, string text, int count,
        [Out] ushort[] glyphs, uint flags);
}
