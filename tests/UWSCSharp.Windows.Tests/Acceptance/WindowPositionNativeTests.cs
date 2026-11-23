using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class WindowPositionNativeTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf", true)]
    [InlineData("UWSCSharp.Wpf", true, 90)]
    [InlineData("UWSCSharp.Cli", false, -100000, -100000)]
    [InlineData("UWSCSharp.Wpf", false, -100000, -100000)]
    [InlineData("UWSCSharp.Cli", false, 100000, 100000)]
    [InlineData("UWSCSharp.Wpf", false, 100000, 100000)]
    [InlineData("UWSCSharp.Wpf", true, 100000, 100000)]
    [InlineData("UWSCSharp.Wpf", true, 240, 200, true)]
    public async Task FrontendCalledScriptMovesVisibleStopWindow(
        string frontend, bool showLog = false, int stopY = 240,
        int stopX = 200, bool hideLog = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        DirectoryInfo directory =
            Directory.CreateTempSubdirectory("uwsc-position-call-");
        string title = "uwsc-position-call-" + Guid.NewGuid();
        const string stopTitle = "UWSC# 再生コントロール";
        try
        {
            string main = Path.Combine(directory.FullName, "main.uws");
            await File.WriteAllTextAsync(main, $$"""
                OPTION DLGTITLE="{{title}}"
                STOPFORM(TRUE,80,90)
                {{(showLog ? "LOGPRINT(TRUE,100,120,400,220)" : "")}}
                MSGBOX("配置前")
                {{(hideLog ? "LOGPRINT(FALSE)" : "")}}
                CALL child
                {{(hideLog ? "LOGPRINT(TRUE)" : "")}}
                OPTION DLGTITLE="{{title}}-after"
                MSGBOX("配置後")
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "child.uws"),
                FormattableString.Invariant($$"""
                    OPTION POSITION=300,500
                    OPTION DLGTITLE="{{title}}-intermediate"
                    MSGBOX("最初の配置後")
                    OPTION POSITION={{stopX}},{{stopY}}
                    """),
                new UTF8Encoding(true));
            string executable = FixturePaths.Get(
                frontend, frontend + ".exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory.FullName
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            try
            {
                nint before = await MessageBoxIntegrationTests.FindWindowAsync(
                    title, process.Id);
                nint stop = await MessageBoxIntegrationTests.FindWindowAsync(
                    stopTitle, process.Id);
                AssertPosition(stop, 80, 90);
                (int _, int _, int width, int height) =
                    WindowsScriptWindowLayout.ReadBounds(stop);
                nint log = 0;
                if (showLog)
                {
                    log = await MessageBoxIntegrationTests.FindWindowAsync(
                        "UWSC# ログ", process.Id);
                    AssertPosition(log, 100, 120);
                }
                Assert.True(Native.PostMessageW(before, 0x10, 0, 0));

                nint intermediate = await MessageBoxIntegrationTests
                    .FindWindowAsync(title + "-intermediate", process.Id);
                AssertPosition(stop, 300, 500);
                if (showLog)
                {
                    Assert.Equal(!hideLog, Native.IsWindowVisible(log));
                    Assert.Equal((300, 280, 400, 220),
                        WindowsScriptWindowLayout.ReadBounds(log));
                }

                Assert.True(Native.PostMessageW(intermediate, 0x10, 0, 0));

                nint after = await MessageBoxIntegrationTests.FindWindowAsync(
                    title + "-after", process.Id);
                Assert.Equal(stopTitle, MessageBoxIntegrationTests.ReadText(stop));
                if (stopX is -100000 or 100000)
                {
                    using var dpi = new PhysicalDpiScope();
                    var monitor = new Native.MonitorInfo
                    {
                        Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
                    };
                    nint nearest = Native.MonitorFromPoint(new(stopX, stopY), 2);
                    Assert.True(Native.GetMonitorInfoW(nearest, ref monitor));
                    int x = stopX < 0 ? monitor.Work.Left
                        : monitor.Work.Right - width;
                    int y = stopY < 0 ? monitor.Work.Top
                        : monitor.Work.Bottom - height;
                    AssertPosition(stop, x, y);
                    Assert.Equal((x, y, width, height),
                        WindowsScriptWindowLayout.ReadBounds(stop));
                    if (showLog)
                    {
                        Assert.True(Native.IsWindowVisible(log));
                        Assert.Equal(
                            (Math.Min(x, monitor.Work.Right - 400), y - 220, 400, 220),
                            WindowsScriptWindowLayout.ReadBounds(log));
                    }
                }
                else
                {
                    AssertPosition(stop, stopX, stopY);
                    if (showLog)
                    {
                        int logY = stopY == 240 ? 20 : stopY + height;
                        Assert.True(Native.IsWindowVisible(log));
                        Assert.Equal((stopX, logY, 400, 220),
                            WindowsScriptWindowLayout.ReadBounds(log));
                    }
                }
                Assert.True(Native.PostMessageW(after, 0x10, 0, 0));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, process.ExitCode);
                Assert.False(Native.IsWindow(stop));
                if (showLog)
                {
                    Assert.False(Native.IsWindow(log));
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        static void AssertPosition(nint window, int expectedX, int expectedY)
        {
            Assert.True(Native.IsWindowVisible(window));
            (int x, int y, _, _) = WindowsScriptWindowLayout.ReadBounds(window);
            Assert.Equal((expectedX, expectedY), (x, y));
        }
    }

    [Fact]
    public async Task CalledScriptMovesTheParentsHiddenStopWindow()
    {
        await using var host = new WindowsStopFormHost(
            new CallbackScriptHost(_ => { }), () => { });
        var interpreter = new Interpreter();
        await interpreter.RunAsync("OPTION POSITION=80,90", host);
        nint handle = await host.GetWindowHandleAsync(default);
        nint foreground = Native.GetForegroundWindow();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("uwsc-position-");
        try
        {
            string main = Path.Combine(directory.FullName, "main.uws");
            string child = Path.Combine(directory.FullName, "child.uws");
            await File.WriteAllTextAsync(main, "CALL child");
            await File.WriteAllTextAsync(child, "OPTION POSITION=200,240");
            await interpreter.RunAsync(ScriptCompiler.CompileFile(main), host);

            Assert.Equal(handle, await host.GetWindowHandleAsync(default));
            (int x, int y, _, _) = WindowsScriptWindowLayout.ReadBounds(handle);
            Assert.Equal((200, 240), (x, y));
            Assert.False(Native.IsWindowVisible(handle));
            Assert.Equal(foreground, Native.GetForegroundWindow());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PublicOptionMovesOnlyTheOwnedHiddenStopWindowThroughLogging()
    {
        List<string> output = [];
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(output.Add), () => { });
        var logger = new ScriptLogHost(host,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"), maximumLines: 0);
        nint foreground = Native.GetForegroundWindow();
        await new Interpreter().RunAsync("OPTION POSITION=200.75,240.75\nPRINT 1", logger);
        nint handle = await host.GetWindowHandleAsync(default);
        (int x, int y, int width, int height) = WindowsScriptWindowLayout.ReadBounds(handle);
        Assert.Equal((200, 240), (x, y));
        Assert.True(width > 0 && height > 0);
        Assert.False(Native.IsWindowVisible(handle));
        Assert.Equal(foreground, Native.GetForegroundWindow());
        Assert.Equal(["1"], output);
        await host.DisposeAsync();
        Assert.False(Native.IsWindow(handle));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.SetWindowPositionAsync(new(1, 2), default).AsTask());
    }

    [Fact]
    public async Task InvalidSecondCoordinateKeepsPreviousNativePosition()
    {
        await using var host = new WindowsStopFormHost(new CallbackScriptHost(_ => { }), () => { });
        var interpreter = new Interpreter();
        await interpreter.RunAsync("OPTION POSITION=80,90", host);
        nint handle = await host.GetWindowHandleAsync(default);
        (int X, int Y, int Width, int Height) before = WindowsScriptWindowLayout.ReadBounds(handle);
        await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync("OPTION POSITION=200,2147483648", host));
        Assert.Equal(before, WindowsScriptWindowLayout.ReadBounds(handle));
        Assert.False(Native.IsWindowVisible(handle));
    }
}
